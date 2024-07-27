// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;

namespace Antelcat.DependencyInjectionEx.ServiceLookup
{
    [DebuggerDisplay("{DebuggerToString(),nq}")]
    [DebuggerTypeProxy(typeof(ServiceProviderEngineScopeDebugView))]
    internal sealed class ServiceProviderEngineScope(ServiceProvider provider, bool isRootScope) : IServiceScope,
        IServiceProvider, IKeyedServiceProvider, IAsyncDisposable, IServiceScopeFactory
    {
        // For testing and debugging only
        internal IList<object> Disposables => disposables ?? (IList<object>)Array.Empty<object>();

        private bool _disposed;
        private List<object>? disposables;

        internal Dictionary<ServiceCacheKey, object?> ResolvedServices { get; } = new();

        internal bool Disposed => _disposed;

        // This lock protects state on the scope, in particular, for the root scope, it protects
        // the list of disposable entries only, since ResolvedServices are cached on CallSites
        // For other scopes, it protects ResolvedServices and the list of disposables
        internal object Sync => ResolvedServices;

        public bool IsRootScope { get; } = isRootScope;

        internal ServiceProvider RootProvider { get; } = provider;

        public object? GetService(Type serviceType)
        {
            if (_disposed)
            {
                ThrowHelper.ThrowObjectDisposedException();
            }

            return RootProvider.GetService(ServiceIdentifier.FromServiceType(serviceType), this);
        }

        public object? GetKeyedService(Type serviceType, object? serviceKey)
        {
            if (_disposed)
            {
                ThrowHelper.ThrowObjectDisposedException();
            }

            return RootProvider.GetKeyedService(serviceType, serviceKey, this);
        }

        public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
        {
            if (_disposed)
            {
                ThrowHelper.ThrowObjectDisposedException();
            }

            return RootProvider.GetRequiredKeyedService(serviceType, serviceKey, this);
        }

        public IServiceProvider ServiceProvider => this;

        public IServiceScope CreateScope() => RootProvider.CreateScope();

        [return: NotNullIfNotNull(nameof(service))]
        internal object? CaptureDisposable(object? service)
        {
            if (ReferenceEquals(this, service) || !(service is IDisposable || service is IAsyncDisposable))
            {
                return service;
            }

            bool disposed = false;
            lock (Sync)
            {
                if (_disposed)
                {
                    disposed = true;
                }
                else
                {
                    disposables ??= new List<object>();

                    disposables.Add(service);
                }
            }

            // Don't run customer code under the lock
            if (!disposed) return service;
            if (service is IDisposable disposable)
            {
                disposable.Dispose();
            }
            else
            {
                // sync over async, for the rare case that an object only implements IAsyncDisposable and may end up starving the thread pool.
                object? localService = service; // copy to avoid closure on other paths
                Task.Run(() => ((IAsyncDisposable)localService).DisposeAsync().AsTask()).GetAwaiter().GetResult();
            }

            ThrowHelper.ThrowObjectDisposedException();

            return service;
        }

        public void Dispose()
        {
            List<object>? toDispose = BeginDispose();

            if (toDispose == null) return;
            for (int i = toDispose.Count - 1; i >= 0; i--)
            {
                if (toDispose[i] is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                else
                {
                    throw new InvalidOperationException(SR.Format(SR.AsyncDisposableServiceDispose, TypeNameHelper.GetTypeDisplayName(toDispose[i])));
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            List<object>? toDispose = BeginDispose();

            if (toDispose == null) return default;
            try
            {
                for (int i = toDispose.Count - 1; i >= 0; i--)
                {
                    object disposable = toDispose[i];
                    if (disposable is IAsyncDisposable asyncDisposable)
                    {
                        ValueTask vt = asyncDisposable.DisposeAsync();
                        if (!vt.IsCompletedSuccessfully)
                        {
                            return Await(i, vt, toDispose);
                        }

                        // If its a IValueTaskSource backed ValueTask,
                        // inform it its result has been read so it can reset
                        vt.GetAwaiter().GetResult();
                    }
                    else
                    {
                        ((IDisposable)disposable).Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                return new ValueTask(Task.FromException(ex));
            }

            return default;

            static async ValueTask Await(int i, ValueTask vt, List<object> toDispose)
            {
                await vt.ConfigureAwait(false);
                // vt is acting on the disposable at index i,
                // decrement it and move to the next iteration
                i--;

                for (; i >= 0; i--)
                {
                    object disposable = toDispose[i];
                    if (disposable is IAsyncDisposable asyncDisposable)
                    {
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        ((IDisposable)disposable).Dispose();
                    }
                }
            }
        }

        private List<object>? BeginDispose()
        {
            lock (Sync)
            {
                if (_disposed)
                {
                    return null;
                }

                // Track statistics about the scope (number of disposable objects and number of disposed services)
                DependencyInjectionEventSource.Log.ScopeDisposed(RootProvider.GetHashCode(), ResolvedServices.Count, disposables?.Count ?? 0);

                // We've transitioned to the disposed state, so future calls to
                // CaptureDisposable will immediately dispose the object.
                // No further changes to _state.Disposables, are allowed.
                _disposed = true;

            }

            if (IsRootScope && !RootProvider.IsDisposed())
            {
                // If this ServiceProviderEngineScope instance is a root scope, disposing this instance will need to dispose the RootProvider too.
                // Otherwise the RootProvider will never get disposed and will leak.
                // Note, if the RootProvider get disposed first, it will automatically dispose all attached ServiceProviderEngineScope objects.
                RootProvider.Dispose();
            }

            // ResolvedServices is never cleared for singletons because there might be a compilation running in background
            // trying to get a cached singleton service. If it doesn't find it
            // it will try to create a new one which will result in an ObjectDisposedException.
            return disposables;
        }

        internal string DebuggerToString()
        {
            string debugText = $"ServiceDescriptors = {RootProvider.CallSiteFactory.Descriptors.Length}";
            if (!IsRootScope)
            {
                debugText += $", IsScope = true";
            }
            if (_disposed)
            {
                debugText += $", Disposed = true";
            }
            return debugText;
        }

        private sealed class ServiceProviderEngineScopeDebugView(ServiceProviderEngineScope serviceProvider)
        {
            public List<ServiceDescriptor> ServiceDescriptors =>
                [..serviceProvider.RootProvider.CallSiteFactory.Descriptors];

            public List<object> Disposables => [..serviceProvider.Disposables];
            public bool         Disposed    => serviceProvider._disposed;
            public bool         IsScope     => !serviceProvider.IsRootScope;
        }
    }
}
