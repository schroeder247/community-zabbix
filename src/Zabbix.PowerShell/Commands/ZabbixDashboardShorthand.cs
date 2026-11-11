// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Globalization;
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// Resolves the dashboard-template widget shorthands into the widget's
/// own <c>fields</c> before the document binds to <see cref="ZabbixDashboardCreateRequest"/>: <c>graph = '&lt;name&gt;'</c>
/// (or <c>@{ host = ..; name = .. }</c>, scoping a name several hosts share) becomes <c>{type: 6, name: graphid.0}</c>,
/// <c>item = @{ host = ..; key = .. }</c> becomes <c>{type: 4, name: itemid.0}</c> — <c>ZBX_WIDGET_FIELD_TYPE_GRAPH</c>/
/// <c>_ITEM</c> (<c>defines.inc.php</c>, 7.0-8.0), the multiselect fields the graph/item widget forms declare
/// (<c>ui/widgets/graph/includes/WidgetForm.php</c>: <c>CWidgetFieldMultiSelectGraph('graphid')</c>). Only a
/// dictionary document carries them (a JSON document is already wire-shaped). The resolved document is a copy; the
/// caller's hashtable is not changed.
/// </summary>
internal static class ZabbixDashboardShorthand
{
    private const int GraphFieldType = 6;
    private const int ItemFieldType = 4;

    public static bool Uses(IDictionary document)
        => Widgets(document).Any(widget => Value(widget, "graph") is not null || Value(widget, "item") is not null);

    public static async Task<IDictionary> ResolveAsync(IDictionary document, ZabbixClient api, CancellationToken cancellationToken)
    {
        var copy = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in document) copy[entry.Key] = entry.Value;
        if (Value(document, "pages") is IEnumerable pages and not string and not IDictionary)
        {
            var resolvedPages = new List<object?>();
            foreach (object? page in pages) resolvedPages.Add(ZabbixJsonBinder.Unwrap(page) is IDictionary table ? await ResolvePageAsync(table, api, cancellationToken).ConfigureAwait(false) : page);
            copy[Key(document, "pages")!] = resolvedPages;
        }
        return copy;
    }

    private static async Task<IDictionary> ResolvePageAsync(IDictionary page, ZabbixClient api, CancellationToken cancellationToken)
    {
        var copy = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in page) copy[entry.Key] = entry.Value;
        if (Value(page, "widgets") is IEnumerable widgets and not string and not IDictionary)
        {
            var resolvedWidgets = new List<object?>();
            foreach (object? widget in widgets) resolvedWidgets.Add(ZabbixJsonBinder.Unwrap(widget) is IDictionary table ? await ResolveWidgetAsync(table, api, cancellationToken).ConfigureAwait(false) : widget);
            copy[Key(page, "widgets")!] = resolvedWidgets;
        }
        return copy;
    }

    private static async Task<IDictionary> ResolveWidgetAsync(IDictionary widget, ZabbixClient api, CancellationToken cancellationToken)
    {
        object? graph = Value(widget, "graph");
        object? item = Value(widget, "item");
        if (graph is null && item is null) return widget;
        var copy = new Hashtable(StringComparer.OrdinalIgnoreCase);
        var fields = new List<object?>();
        foreach (DictionaryEntry entry in widget)
        {
            string key = entry.Key.ToString() ?? string.Empty;
            if (key.Equals("graph", StringComparison.OrdinalIgnoreCase) || key.Equals("item", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("fields", StringComparison.OrdinalIgnoreCase) && ZabbixJsonBinder.Unwrap(entry.Value) is IEnumerable existing and not string and not IDictionary)
                fields.AddRange(existing.Cast<object?>());
            else if (key.Equals("fields", StringComparison.OrdinalIgnoreCase))
                fields.Add(entry.Value);
            else
                copy[entry.Key] = entry.Value;
        }
        if (graph is not null)
        {
            (string? host, string name) = graph is IDictionary scoped
                ? (Text(scoped, "host"), Text(scoped, "name") ?? throw new ArgumentException("a widget's graph = @{ host; name } needs a 'name'"))
                : (null, graph.ToString() ?? string.Empty);
            var parameters = new ZabbixGraphGetParameters
            {
                Filter = new() { ["name"] = name },
                HostIds = host is null ? null : [await HostIdAsync(host, api, cancellationToken).ConfigureAwait(false)],
            };
            IReadOnlyList<ZabbixGraph> graphs = await api.Graphs.GetAsync(parameters, cancellationToken).ConfigureAwait(false);
            ulong graphId = (graphs.Count > 0 ? graphs[0].GraphId : null)
                ?? throw new ItemNotFoundException(host is null ? $"no graph is named '{name}'" : $"no graph is named '{name}' on host '{host}'");
            fields.Add(Field(GraphFieldType, "graphid.0", graphId.ToString(CultureInfo.InvariantCulture)));
        }
        if (item is not null)
        {
            IDictionary reference = item as IDictionary ?? throw new ArgumentException("a widget's item shorthand is @{ host = ..; key = .. }");
            string host = Text(reference, "host") ?? throw new ArgumentException("a widget's item = @{ host; key } needs a 'host'");
            string key = Text(reference, "key") ?? throw new ArgumentException("a widget's item = @{ host; key } needs a 'key'");
            var parameters = new ZabbixItemGetParameters
            {
                Filter = new() { ["key_"] = key },
                HostIds = [await HostIdAsync(host, api, cancellationToken).ConfigureAwait(false)],
            };
            IReadOnlyList<ZabbixItem> items = await api.Items.GetAsync(parameters, cancellationToken).ConfigureAwait(false);
            ulong itemId = (items.Count > 0 ? items[0].ItemId : null) ?? throw new ItemNotFoundException($"no item has the key '{key}' on host '{host}'");
            fields.Add(Field(ItemFieldType, "itemid.0", itemId.ToString(CultureInfo.InvariantCulture)));
        }
        copy["fields"] = fields;
        return copy;
    }

    private static async Task<ulong> HostIdAsync(string host, ZabbixClient api, CancellationToken cancellationToken)
    {
        var parameters = new ZabbixHostGetParameters { Filter = new() { ["host"] = host } };
        IReadOnlyList<ZabbixHost> hosts = await api.Hosts.GetAsync(parameters, cancellationToken).ConfigureAwait(false);
        return (hosts.Count > 0 ? hosts[0].HostId : null) ?? throw new ItemNotFoundException($"no host is named '{host}'");
    }

    private static Hashtable Field(int type, string name, string value)
        => new(StringComparer.OrdinalIgnoreCase) { ["type"] = type, ["name"] = name, ["value"] = value };

    private static IEnumerable<IDictionary> Widgets(IDictionary document)
    {
        if (Value(document, "pages") is not IEnumerable pages || pages is string or IDictionary) yield break;
        foreach (object? page in pages)
        {
            if (ZabbixJsonBinder.Unwrap(page) is not IDictionary table || Value(table, "widgets") is not IEnumerable widgets || widgets is string or IDictionary) continue;
            foreach (object? widget in widgets)
                if (ZabbixJsonBinder.Unwrap(widget) is IDictionary entry) yield return entry;
        }
    }

    // A PowerShell @{} is case-insensitive, an [ordered]@{} or a caller's own dictionary may not be: the key is found
    // ordinal-ignore-case either way.
    private static object? Key(IDictionary table, string key)
        => table.Keys.Cast<object>().FirstOrDefault(k => string.Equals(k.ToString(), key, StringComparison.OrdinalIgnoreCase));

    private static object? Value(IDictionary table, string key)
        => Key(table, key) is { } found ? ZabbixJsonBinder.Unwrap(table[found]) : null;

    private static string? Text(IDictionary table, string key) => Value(table, key)?.ToString();
}
