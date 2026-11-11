// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Proxy;

/// <summary>
/// Provides the provider/merge mechanics every <see cref="ZabbixProxyDataCallback"/> caller shares: asking a set of
/// providers for a page and folding the answers into one (<see cref="ZabbixActiveProxy"/>'s upload loop,
/// <see cref="ZabbixPassiveProxy"/>'s pull conversation, and any caller composing its own turn directly over
/// <see cref="ZabbixProxyUplink"/>, e.g. the PowerShell module's active-proxy session), so all of them share one implementation.
/// </summary>
public static class ZabbixProxyDataProviders
{
    /// <summary>
    /// Asks every provider in <paramref name="providers"/> for a page and returns the ones that answered with one. A
    /// provider that throws is reported to <paramref name="onFailed"/> (a proxy host logs it as event 7017) and skipped
    /// — one caller's scriptblock must not stop the turn.
    /// </summary>
    public static ValueTask<IReadOnlyList<ZabbixProxyDataPage>> AskAsync(IReadOnlyList<ZabbixProxyDataCallback> providers, ZabbixProxyConfig config,
        Action<Exception>? onFailed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        return AskCoreAsync(providers, config, onFailed, cancellationToken);
    }

    private static async ValueTask<IReadOnlyList<ZabbixProxyDataPage>> AskCoreAsync(IReadOnlyList<ZabbixProxyDataCallback> providers, ZabbixProxyConfig config,
        Action<Exception>? onFailed, CancellationToken cancellationToken)
    {
        var pages = new List<ZabbixProxyDataPage>(providers.Count);
        foreach (ZabbixProxyDataCallback provider in providers)
        {
            try
            {
                ZabbixProxyDataPage? page = await provider(config, cancellationToken).ConfigureAwait(false);
                if (page is not null && !page.IsEmpty) pages.Add(page);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                onFailed?.Invoke(failure);
            }
        }
        return pages;
    }

    /// <summary>Merges <paramref name="extra"/> pages' lists onto <paramref name="page"/>'s, in order; empty lists stay <see langword="null"/>.</summary>
    public static ZabbixProxyDataPage Merge(ZabbixProxyDataPage page, IReadOnlyList<ZabbixProxyDataPage> extra)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(extra);
        if (extra.Count == 0) return page;
        return page with
        {
            History = Concat(page.History, extra, static p => p.History),
            Discovery = Concat(page.Discovery, extra, static p => p.Discovery),
            Autoregistration = Concat(page.Autoregistration, extra, static p => p.Autoregistration),
            Availability = Concat(page.Availability, extra, static p => p.Availability),
            Tasks = Concat(page.Tasks, extra, static p => p.Tasks),
        };

        static IReadOnlyList<T>? Concat<T>(IReadOnlyList<T>? first, IReadOnlyList<ZabbixProxyDataPage> extra, Func<ZabbixProxyDataPage, IReadOnlyList<T>?> pick)
        {
            List<T>? merged = first is { Count: > 0 } ? [.. first] : null;
            foreach (ZabbixProxyDataPage page in extra)
            {
                if (pick(page) is not { Count: > 0 } more) continue;
                merged ??= [];
                merged.AddRange(more);
            }
            return merged;
        }
    }
}
