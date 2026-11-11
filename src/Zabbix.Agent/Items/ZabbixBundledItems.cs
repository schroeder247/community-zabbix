// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Hosting;

namespace Community.Zabbix.Items;

/// <summary>
/// The bundled items (docs/adr/0009-bundled-items.md): every family provider and the two agent keys beside the four
/// every agent answers, added to an agent's table as defaults, so a handler or a provider of the same name replaces them.
/// </summary>
internal static class ZabbixBundledItems
{
    /// <summary>The family providers, sampling through the agent's collector and on its clock.</summary>
    public static IReadOnlyList<ZabbixItemProvider> Providers(ZabbixItemCollector collector) =>
    [
#pragma warning disable CA2000 // these own nothing: the collector they share is its owner's to dispose of
        new ZabbixSystemItems(collector),
        new ZabbixProcessItems(collector),
        new ZabbixFileSystemItems(collector),
#pragma warning restore CA2000
        new ZabbixFileItems(collector.Time),
        new ZabbixNetworkItems(collector.Files, collector.Time),
        new ZabbixWebItems(collector.Time),
    ];

    /// <summary>Adds the bundled keys under every name <paramref name="handlers"/> leaves free.</summary>
    /// <param name="handlers">The table.</param>
    /// <param name="hostMetadata">What <c>agent.hostmetadata</c> answers: the active agent's <c>HostMetadata</c>, empty when none.</param>
    /// <param name="collector">The agent's collector.</param>
    public static void AddTo(ZabbixItemDictionary handlers, string? hostMetadata, ZabbixItemCollector collector)
    {
        handlers.TryAdd("agent.variant", _ => ZabbixWire.AgentVariant.ToString(System.Globalization.CultureInfo.InvariantCulture));
        handlers.TryAdd("agent.hostmetadata", _ => hostMetadata ?? string.Empty);
        foreach (ZabbixItemProvider provider in Providers(collector))
        {
            foreach (ZabbixItemDefinition item in provider.Items) handlers.TryAdd(item.KeyName, provider.GetValueAsync);
        }
    }
}
