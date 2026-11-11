// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Community.Zabbix.Proxy;

/// <summary>
/// Represents the configuration a proxy holds: the server's tables, merged revision by revision, frozen per revision. The
/// server sends a full sync on the first request (<c>full_sync</c>, revision 0) and deltas after it: only the hosts that
/// changed, each with all of its items; <c>del_hostids</c> for hosts that left; <c>globalmacro</c> whole when it
/// changed; <c>hostmacro</c>/<c>hosts_templates</c> for the hosts whose macros changed and <c>del_macro_hostids</c>
/// (upstream's <c>ZBX_PROTO_TAG_REMOVED_HOSTIDS</c>/<c>ZBX_PROTO_TAG_REMOVED_MACRO_HOSTIDS</c>, zbxjson.h 7.0:200-201, 8.0:204-205);
/// and, for a proxy in a group, <c>proxy_group.hostmap_revision</c> (<c>proxyconfigread.c</c>). <see cref="Merge"/>
/// applies a reply as the upstream proxy's <c>proxyconfig_sync_hosts</c> does — a delta's host replaces that host and all
/// its items; a host not in the delta keeps its own — and freezes the result, so a reader holds one immutable revision.
/// </summary>
/// <remarks>
/// Only the tables the SDK acts on are kept: hosts, items, and the macros that resolve <c>trapper_hosts</c>. A proxy that
/// polls (interfaces, preprocessing, discovery rules, web scenarios) is not what this type models.
/// </remarks>
public sealed class ZabbixProxyConfig
{
    private ZabbixProxyConfig(
        long configRevision,
        long hostmapRevision,
        FrozenDictionary<ulong, ZabbixProxyConfigHost> hosts,
        FrozenDictionary<ulong, ZabbixProxyConfigItem> items,
        IReadOnlyDictionary<string, string> globalMacros,
        FrozenDictionary<ulong, IReadOnlyDictionary<string, string>> hostMacros,
        FrozenDictionary<ulong, ImmutableArray<ulong>> templates,
        string agentTimeout)
    {
        ConfigRevision = configRevision;
        AgentTimeout = agentTimeout;
        HostmapRevision = hostmapRevision;
        Hosts = hosts;
        Items = items;
        GlobalMacros = globalMacros;
        HostMacros = hostMacros;
        Templates = templates;
        var byKey = new Dictionary<(string Host, string Key), ZabbixProxyConfigItem>(items.Count);
        var byHost = new Dictionary<ulong, List<ZabbixProxyConfigItem>>(hosts.Count);
        foreach (ZabbixProxyConfigItem item in items.Values)
        {
            if (hosts.TryGetValue(item.HostId, out ZabbixProxyConfigHost? host)) byKey[(host.Host, item.Key)] = item;
            if (!byHost.TryGetValue(item.HostId, out List<ZabbixProxyConfigItem>? held)) byHost[item.HostId] = held = [];
            held.Add(item);
        }
        ItemsByKey = byKey.ToFrozenDictionary();
        ItemsByHost = byHost.ToFrozenDictionary(pair => pair.Key, pair => (IReadOnlyList<ZabbixProxyConfigItem>)[.. pair.Value]);
        HostsByName = hosts.Values.ToFrozenDictionary(host => host.Host, StringComparer.Ordinal);
    }

    /// <summary>Nothing held: revision 0, which asks the server for a full sync.</summary>
    public static ZabbixProxyConfig Empty { get; } = new(0, 0,
        FrozenDictionary<ulong, ZabbixProxyConfigHost>.Empty, FrozenDictionary<ulong, ZabbixProxyConfigItem>.Empty,
        FrozenDictionary<string, string>.Empty, FrozenDictionary<ulong, IReadOnlyDictionary<string, string>>.Empty, FrozenDictionary<ulong, ImmutableArray<ulong>>.Empty, string.Empty);

    /// <summary>The configuration revision held: what the next request says it has.</summary>
    public long ConfigRevision { get; }

    /// <summary>The host map's revision (a proxy in a proxy group); 0 otherwise.</summary>
    public long HostmapRevision { get; }

    /// <summary>The hosts, by id.</summary>
    public IReadOnlyDictionary<ulong, ZabbixProxyConfigHost> Hosts { get; }

    /// <summary>The items, by id.</summary>
    public IReadOnlyDictionary<ulong, ZabbixProxyConfigItem> Items { get; }

    /// <summary>The items by (host name, key), the key compared literally as Zabbix does.</summary>
    public IReadOnlyDictionary<(string Host, string Key), ZabbixProxyConfigItem> ItemsByKey { get; }

    /// <summary>Each host's items, by host id: what <c>active checks</c> answers a host from without a scan of every item.</summary>
    internal FrozenDictionary<ulong, IReadOnlyList<ZabbixProxyConfigItem>> ItemsByHost { get; }

    /// <summary>The hosts by their technical name: what <c>active checks</c> looks a requesting agent up by.</summary>
    public IReadOnlyDictionary<string, ZabbixProxyConfigHost> HostsByName { get; }

    /// <summary>The global user macros, <c>{$NAME}</c> → value.</summary>
    public IReadOnlyDictionary<string, string> GlobalMacros { get; }

    /// <summary>Host and template user macros, by host id.</summary>
    public IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, string>> HostMacros { get; }

    /// <summary>The templates linked to each host (and template), by host id.</summary>
    public IReadOnlyDictionary<ulong, ImmutableArray<ulong>> Templates { get; }

    /// <summary>
    /// The Zabbix agent check timeout this proxy applies to an item without its own, verbatim; empty until the server sent
    /// one. The server sends the proxy's own override when it has one, else the global value: 7.0 as the <c>config</c>
    /// table's <c>timeout_zabbix_agent</c> column, 7.4 and 8.0 as the <c>settings</c> row of that name
    /// (<c>proxyconfigread.c</c> 7.0:385-453, 8.0:370-470). <c>active checks</c> answers it for such an item, as the
    /// server's <c>timeout_orig</c> does (<c>dbconfig.c</c> <c>dc_get_global_item_type_timeout</c>).
    /// </summary>
    public string AgentTimeout { get; }

    /// <summary>Whether a reply was a full sync; <see langword="false"/> for a delta or an empty reply.</summary>
    /// <param name="reply">The server's reply.</param>
    /// <returns>Whether it replaces everything.</returns>
    public static bool IsFullSync(JsonElement reply)
        => reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("full_sync", out JsonElement full)
           && (full.ValueKind == JsonValueKind.True || Long(full) == 1);

    /// <summary>
    /// Applies a server reply: <c>{}</c> (nothing changed) returns this configuration; a full sync replaces it; a delta
    /// merges into it by id. The result is a new frozen configuration; this one is not changed.
    /// </summary>
    /// <param name="reply">The configuration frame's root.</param>
    /// <returns>The configuration after the reply.</returns>
    /// <exception cref="JsonException">The reply is not an object, a table's fields are not names or its rows do not match them, or two hosts
    /// would hold one technical name.</exception>
    public ZabbixProxyConfig Merge(JsonElement reply)
    {
        if (reply.ValueKind != JsonValueKind.Object) throw new JsonException($"A proxy configuration is a JSON object, not {reply.ValueKind}.");
        if (!reply.EnumerateObject().Any()) return this;                                  // no configuration updates

        ZabbixProxyConfig basis = IsFullSync(reply) ? Empty : this;
        JsonElement data = reply.TryGetProperty("data", out JsonElement tables) && tables.ValueKind == JsonValueKind.Object ? tables : default;
        var draft = new Draft(basis);
        draft.ApplyHosts(data);
        draft.ApplyItems(data);
        draft.DeleteHosts(reply);
        draft.ApplyMacros(data);
        draft.DeleteMacros(reply);
        draft.RefuseSharedNames();
        return new ZabbixProxyConfig(ConfigRevisionOf(reply, basis), HostmapRevisionOf(reply, basis), draft.Hosts.ToFrozenDictionary(), draft.Items.ToFrozenDictionary(),
            draft.GlobalMacros, draft.HostMacros.ToFrozenDictionary(), draft.Templates.ToFrozenDictionary(), AgentTimeoutOf(data, basis));
    }

    private static long ConfigRevisionOf(JsonElement reply, ZabbixProxyConfig basis)
        => reply.TryGetProperty("config_revision", out JsonElement revision) && Long(revision) is { } number ? number : basis.ConfigRevision;

    private static long HostmapRevisionOf(JsonElement reply, ZabbixProxyConfig basis)
        => reply.TryGetProperty("proxy_group", out JsonElement group) && group.ValueKind == JsonValueKind.Object
           && group.TryGetProperty("hostmap_revision", out JsonElement map) && Long(map) is { } mapNumber
            ? mapNumber
            : basis.HostmapRevision;

    // The agent timeout: sent with the settings when they changed, kept otherwise.
    private static string AgentTimeoutOf(JsonElement data, ZabbixProxyConfig basis)
    {
        string agentTimeout = basis.AgentTimeout;
        if (TryTable(data, "config", out List<Dictionary<string, JsonElement>>? configRows) && configRows.Count > 0 && Text(configRows[0], "timeout_zabbix_agent") is { } configured)
            agentTimeout = configured;
        if (TryTable(data, "settings", out List<Dictionary<string, JsonElement>>? settingRows))
            foreach (Dictionary<string, JsonElement> row in settingRows)
                if (Text(row, "name") == "timeout_zabbix_agent" && Text(row, "value_str") is { } setting) agentTimeout = setting;
        return agentTimeout;
    }

    /// <summary>The configuration being built from a basis and a reply, one table at a time, in upstream's order.</summary>
    private sealed class Draft(ZabbixProxyConfig basis)
    {
        public Dictionary<ulong, ZabbixProxyConfigHost> Hosts { get; } = new(basis.Hosts);

        public Dictionary<ulong, ZabbixProxyConfigItem> Items { get; } = new(basis.Items);

        public Dictionary<ulong, IReadOnlyDictionary<string, string>> HostMacros { get; } = basis.HostMacros.ToDictionary(pair => pair.Key, pair => pair.Value);

        public Dictionary<ulong, ImmutableArray<ulong>> Templates { get; } = new(basis.Templates);

        public IReadOnlyDictionary<string, string> GlobalMacros { get; private set; } = basis.GlobalMacros;

        // Hosts: a host in the reply replaces the one held, and its items are exactly the reply's.
        public void ApplyHosts(JsonElement data)
        {
            if (!TryTable(data, "hosts", out List<Dictionary<string, JsonElement>>? hostRows)) return;
            var updated = new HashSet<ulong>();
            foreach (Dictionary<string, JsonElement> row in hostRows)
            {
                if (Id(row, "hostid") is not { } hostId || Text(row, "host") is not { } host) continue;
                Hosts[hostId] = new ZabbixProxyConfigHost(hostId, host, Text(row, "name") ?? host, (ZabbixProxyHostStatus)(Long(row, "status") ?? 0));
                updated.Add(hostId);
            }
            RemoveItemsOf(Items, updated);
        }

        public void ApplyItems(JsonElement data)
        {
            if (!TryTable(data, "items", out List<Dictionary<string, JsonElement>>? itemRows)) return;
            foreach (Dictionary<string, JsonElement> row in itemRows)
            {
                if (Id(row, "itemid") is not { } itemId || Id(row, "hostid") is not { } hostId || Text(row, "key_") is not { } key) continue;
                Items[itemId] = new ZabbixProxyConfigItem(itemId, hostId, key, (ZabbixProxyItemType)(Long(row, "type") ?? 0), (ZabbixProxyItemStatus)(Long(row, "status") ?? 0),
                    [.. ZabbixPeer.FromReply(Text(row, "trapper_hosts") ?? string.Empty)], Long(row, "allow_traps") == 1, ZabbixInterval.FromReply(Text(row, "delay") ?? string.Empty),
                    Text(row, "timeout") is { Length: > 0 } timeout ? ZabbixDuration.FromReply(timeout) : null);
            }
        }

        public void DeleteHosts(JsonElement reply)
        {
            var removed = new HashSet<ulong>(Ids(reply, "del_hostids"));
            foreach (ulong hostId in removed)
            {
                Hosts.Remove(hostId);
                ForgetMacrosOf(hostId);
            }
            RemoveItemsOf(Items, removed);                                                 // one pass over the items, not one per host
        }

        // Macros: the global table arrives whole; host macros and template links arrive for the hosts whose macros changed.
        public void ApplyMacros(JsonElement data)
        {
            if (TryTable(data, "globalmacro", out List<Dictionary<string, JsonElement>>? globalRows))
                GlobalMacros = Macros(globalRows).ToFrozenDictionary(StringComparer.Ordinal);
            bool hostMacrosSent = TryTable(data, "hostmacro", out List<Dictionary<string, JsonElement>>? macroRows);
            bool templatesSent = TryTable(data, "hosts_templates", out List<Dictionary<string, JsonElement>>? templateRows);
            if (!hostMacrosSent && !templatesSent) return;
            macroRows ??= [];
            templateRows ??= [];
            foreach (ulong hostId in macroRows.Concat(templateRows).Select(HostOf).ToHashSet()) ForgetMacrosOf(hostId);
            foreach (IGrouping<ulong, Dictionary<string, JsonElement>> host in macroRows.GroupBy(HostOf))
                HostMacros[host.Key] = Macros(host).ToFrozenDictionary(StringComparer.Ordinal);
            foreach (IGrouping<ulong, Dictionary<string, JsonElement>> host in templateRows.GroupBy(HostOf))
                Templates[host.Key] = [.. host.Select(row => Id(row, "templateid") ?? 0UL).Where(id => id != 0)];
        }

        // Host id 0 is the global host: upstream proxyconfigwrite.c (7.0 :2226, 8.0 :2431) reads it as "the global macros
        // are gone" and deletes them after the tables are applied, as this does.
        public void DeleteMacros(JsonElement reply)
        {
            foreach (ulong hostId in Ids(reply, "del_macro_hostids"))
            {
                if (hostId == 0) GlobalMacros = FrozenDictionary<string, string>.Empty;
                else ForgetMacrosOf(hostId);
            }
        }

        // A host's technical name is unique on the server (and in the upstream proxy's database), and agents and senders
        // name a host by it; a configuration that gives two hosts one name cannot be held, and is refused as a whole.
        public void RefuseSharedNames()
        {
            var named = new Dictionary<string, ulong>(Hosts.Count, StringComparer.Ordinal);
            foreach (ZabbixProxyConfigHost host in Hosts.Values)
                if (!named.TryAdd(host.Host, host.HostId))
                    throw new JsonException($"Hosts {named[host.Host]} and {host.HostId} have one technical name, \"{host.Host}\".");
        }

        private void ForgetMacrosOf(ulong hostId)
        {
            HostMacros.Remove(hostId);
            Templates.Remove(hostId);
        }

        private static ulong HostOf(Dictionary<string, JsonElement> row) => Id(row, "hostid") ?? 0UL;
    }

    private static void RemoveItemsOf(Dictionary<ulong, ZabbixProxyConfigItem> items, HashSet<ulong> hostIds)
    {
        if (hostIds.Count == 0) return;
        foreach (ulong itemId in items.Values.Where(item => hostIds.Contains(item.HostId)).Select(item => item.ItemId).ToList()) items.Remove(itemId);
    }

    private static IEnumerable<KeyValuePair<string, string>> Macros(IEnumerable<Dictionary<string, JsonElement>> rows)
    {
        foreach (Dictionary<string, JsonElement> row in rows)
            if (Text(row, "macro") is { } macro) yield return new KeyValuePair<string, string>(macro, Text(row, "value") ?? string.Empty);
    }

    // A table is {"fields":[…],"data":[[…],…]}; each row becomes a field → cell map.
    private static bool TryTable(JsonElement data, string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out List<Dictionary<string, JsonElement>>? rows)
    {
        rows = null;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out JsonElement table) || table.ValueKind != JsonValueKind.Object) return false;
        if (!table.TryGetProperty("fields", out JsonElement fields) || fields.ValueKind != JsonValueKind.Array) throw new JsonException($"The {name} table has no fields array.");
        string[] names = [.. fields.EnumerateArray().Select(field => field.ValueKind == JsonValueKind.String
            ? field.GetString()!
            : throw new JsonException($"The {name} table's fields are not all names: {field.ValueKind}."))];
        rows = [];
        if (!table.TryGetProperty("data", out JsonElement cells) || cells.ValueKind != JsonValueKind.Array) return true;
        foreach (JsonElement row in cells.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != names.Length)
                throw new JsonException($"A {name} row does not match the table's {names.Length} fields.");
            var record = new Dictionary<string, JsonElement>(names.Length, StringComparer.Ordinal);
            int i = 0;
            foreach (JsonElement cell in row.EnumerateArray()) record[names[i++]] = cell;   // read within Merge, while the reply lives: no clone
            rows.Add(record);
        }
        return true;
    }

    private static IEnumerable<ulong> Ids(JsonElement reply, string name)
    {
        if (!reply.TryGetProperty(name, out JsonElement ids) || ids.ValueKind != JsonValueKind.Array) yield break;
        foreach (JsonElement id in ids.EnumerateArray())
            if (Id(id) is { } value) yield return value;
    }

    private static string? Text(Dictionary<string, JsonElement> row, string field)
        => row.TryGetValue(field, out JsonElement cell) ? cell.ValueKind switch
        {
            JsonValueKind.String => cell.GetString(),
            JsonValueKind.Number => cell.GetRawText(),
            _ => null,
        } : null;

    private static long? Long(Dictionary<string, JsonElement> row, string field) => row.TryGetValue(field, out JsonElement cell) ? Long(cell) : null;

    private static ulong? Id(Dictionary<string, JsonElement> row, string field) => row.TryGetValue(field, out JsonElement cell) ? Id(cell) : null;

    // The tables carry ids as JSON numbers (7.0 and 8.0) and older senders as strings; both are read.
    private static ulong? Id(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetUInt64(out ulong number) => number,
        JsonValueKind.String when ulong.TryParse(value.GetString(), null, out ulong id) => id,
        _ => null,
    };

    // A number the tables carry as a JSON number or as text.
    private static long? Long(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out long number) => number,
        JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) => number,
        _ => null,
    };
}
