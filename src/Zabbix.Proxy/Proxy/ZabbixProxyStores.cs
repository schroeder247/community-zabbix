// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using Community.Zabbix.Storage;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Proxy;

/// <summary>
/// Holds a proxy's five stores, built and ended together. The history store is the caller's backing when <c>AddStore</c>
/// gave one, else in memory; discovery, auto-registration, interface availability and task results are in memory. Both
/// proxy hosts hold one of these, so the five are completed together at stop and disposed together on every disposal path.
/// </summary>
internal sealed class ZabbixProxyStores : IAsyncDisposable, IDisposable
{
    private ZabbixProxyStores(ZabbixStore<ZabbixValue> history, string name, ZabbixProxyOptions options, ZabbixStoreServices services)
    {
        History = history;
        Discovery = Memory(name + ".discovery", options.SimpleStoragePolicy<ZabbixProxyDiscoveryItem>(), services);
        Autoregistration = Memory(name + ".autoregistration", options.SimpleStoragePolicy<ZabbixProxyAutoregistrationItem>(), services);
        Availability = Memory(name + ".availability", options.SimpleStoragePolicy<ZabbixProxyAvailabilityItem>(), services);
        Tasks = Memory(name + ".tasks", options.SimpleStoragePolicy<ZabbixProxyTaskResult>(), services);
    }

    /// <summary>The stores of the proxy named <paramref name="name"/>, the history store built by <paramref name="history"/> (in memory when <see langword="null"/>) under <paramref name="policy"/>.</summary>
    public static ZabbixProxyStores Create(string name, ZabbixProxyOptions options, Func<ZabbixStorePolicy<ZabbixValue>, ZabbixStoreServices, ZabbixStore<ZabbixValue>>? history, ZabbixStorePolicy<ZabbixValue> policy,
        ILoggerFactory loggers, IMeterFactory? meters, TimeProvider time)
    {
        var services = new ZabbixStoreServices { Name = name, LoggerFactory = loggers, Meters = meters, Time = time };
        return new ZabbixProxyStores(HistoryStore(history, policy, services), name, options, services);
    }

    private static ZabbixStore<ZabbixValue> HistoryStore(Func<ZabbixStorePolicy<ZabbixValue>, ZabbixStoreServices, ZabbixStore<ZabbixValue>>? history, ZabbixStorePolicy<ZabbixValue> policy,
        ZabbixStoreServices services)
    {
        if (history is not null) return history(policy, services);
        return new ZabbixMemoryStore<ZabbixValue>(policy, services);
    }

    /// <summary>
    /// The history store's factory a registration's <see cref="ZabbixProxyOptions.Storage"/> names, bound to the container it
    /// was registered in; <see langword="null"/> for the in-memory default.
    /// </summary>
    public static Func<ZabbixStorePolicy<ZabbixValue>, ZabbixStoreServices, ZabbixStore<ZabbixValue>>? HistoryFactory(ZabbixProxyOptions options, IServiceProvider provider)
        => options.Storage is { } storage ? (policy, services) => storage(provider, policy, services) : null;

    public ZabbixStore<ZabbixValue> History { get; }

    public ZabbixStore<ZabbixProxyDiscoveryItem> Discovery { get; }

    public ZabbixStore<ZabbixProxyAutoregistrationItem> Autoregistration { get; }

    public ZabbixStore<ZabbixProxyAvailabilityItem> Availability { get; }

    public ZabbixStore<ZabbixProxyTaskResult> Tasks { get; }

    /// <summary>Whether any store holds a record not yet leased.</summary>
    public bool AnyPending()
        => History.TryPeek(out _) || Discovery.TryPeek(out _) || Autoregistration.TryPeek(out _) || Availability.TryPeek(out _) || Tasks.TryPeek(out _);

    /// <summary>Nothing more is written: each store's readers drain what it holds and end.</summary>
    public void Complete()
    {
        History.Complete();
        Discovery.Complete();
        Autoregistration.Complete();
        Availability.Complete();
        Tasks.Complete();
    }

    public async ValueTask DisposeAsync()
    {
        await History.DisposeAsync().ConfigureAwait(false);
        await Discovery.DisposeAsync().ConfigureAwait(false);
        await Autoregistration.DisposeAsync().ConfigureAwait(false);
        await Availability.DisposeAsync().ConfigureAwait(false);
        await Tasks.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The synchronous form, for a container disposed synchronously: each store's disposal is started here, and one that
    /// finishes asynchronously (a backing that releases a connection) finishes on its own, its fault observed.
    /// </summary>
    public void Dispose()
    {
        Release(History);
        Release(Discovery);
        Release(Autoregistration);
        Release(Availability);
        Release(Tasks);
    }

    private static void Release(IAsyncDisposable store)
    {
        ValueTask disposing = store.DisposeAsync();
        if (!disposing.IsCompletedSuccessfully) _ = ObserveAsync(disposing.AsTask());
    }

    // Awaited, never rethrown: a fault of a disposal nobody waits for is observed here, not left to UnobservedTaskException.
    private static async Task ObserveAsync(Task disposing) => await disposing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private static ZabbixMemoryStore<T> Memory<T>(string name, ZabbixStorePolicy<T> policy, ZabbixStoreServices services)
        => new(policy, services with { Name = name });
}
