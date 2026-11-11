// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>
/// Types the fields the description states as text (ADR 0013, section 5). The description holds what Zabbix's source
/// states, a string or a list of strings or an output; which .NET type the SDK gives a field of that grammar is the SDK's
/// choice, so it's made here, by the field's object and wire name, and a regeneration of the description keeps it. It runs
/// once, in <see cref="ApiDescription.Read(string, List{EmitProblem})"/> before the records are named, so every emitter
/// (the records, the cmdlets, the views) sees the typed kinds:
/// <list type="bullet">
/// <item><see cref="Interval"/>: an update interval, <c>ZabbixInterval</c>.</item>
/// <item><see cref="Duration"/>: how long an item's history and trends are kept and its check may take, <c>ZabbixDuration</c>.</item>
/// <item><see cref="Port"/>: an interface's port, <c>ZabbixPort</c>.</item>
/// <item><see cref="Peers"/>: the hosts that may send to a trapper, a list of <c>ZabbixPeer</c>.</item>
/// <item><c>output:</c> and <c>list:field:</c>: a get's <c>output</c>, its <c>select</c> options and its <c>sortfield</c>,
/// typed by the record whose fields they name (<c>output:object:Host</c>), or <c>json</c> where the SDK reads that
/// object as JSON.</item>
/// </list>
/// </summary>
internal static class ZabbixTypedFields
{
    /// <summary>An update interval, in the grammar the API checks a delay against (<c>API_ITEM_DELAY</c>).</summary>
    public const string Interval = "interval";

    /// <summary>A length of time, the first part of an update interval: a number with a suffix, or a macro (<c>API_TIME_UNIT</c>).</summary>
    public const string Duration = "duration";

    /// <summary>An interface's port: a number or a macro (<c>API_PORT</c>).</summary>
    public const string Port = "port";

    /// <summary>The hosts that may send to a trapper, as Zabbix's comma-separated list (<c>API_IP_RANGES</c>).</summary>
    public const string Peers = "list:peer";

    /// <summary>A get's <c>output</c> or <c>select</c> option, before the record whose fields it names.</summary>
    public const string Output = "output:";

    /// <summary>A get's <c>sortfield</c>, before the record whose fields it names.</summary>
    public const string SortFields = "list:field:";

    // The fields of an object's records, its read record and its requests, by the object and the field's wire name. An
    // item's, an item prototype's and a discovery rule's delay, timeout and trapper hosts are checked by one rule each
    // (CItemType.php 7.0:160-204), an item's history and trends as lengths of time (CItem.php 7.0:546, CItemPrototype.php
    // 7.0:395-397), and an interface's port as a number or a macro (CHostInterface.php 7.0:826-831).
    private static readonly (string Object, string Wire, string Kind)[] s_objectFields =
    [
        ("item", "delay", Interval),
        ("itemprototype", "delay", Interval),
        ("discoveryrule", "delay", Interval),
        ("discoveryruleprototype", "delay", Interval),
        ("item", "history", Duration),
        ("item", "trends", Duration),
        ("item", "timeout", Duration),
        ("itemprototype", "history", Duration),
        ("itemprototype", "trends", Duration),
        ("itemprototype", "timeout", Duration),
        ("discoveryrule", "timeout", Duration),
        ("discoveryruleprototype", "timeout", Duration),
        ("item", "trapper_hosts", Peers),
        ("itemprototype", "trapper_hosts", Peers),
        ("discoveryrule", "trapper_hosts", Peers),
        ("discoveryruleprototype", "trapper_hosts", Peers),
        ("hostinterface", "port", Port),
    ];

    // The fields of records nested in a request, which name no object, by the record and the field's wire name: the
    // interval, the history and the trends an override of a discovery rule sets (CDiscoveryRule.php 7.0:1257), and the
    // port of each interface a host's, a host prototype's (CHostPrototype.php 7.0:740) and the interfaces' own requests hold.
    private static readonly (string Record, string Wire, string Kind)[] s_nestedFields =
    [
        ("DiscoveryRuleOverrideOperationOpperiodRequest", "delay", Interval),
        ("DiscoveryRulePrototypeOverrideOperationOpperiodRequest", "delay", Interval),
        ("DiscoveryRuleOverrideOperationOphistoryRequest", "history", Duration),
        ("DiscoveryRulePrototypeOverrideOperationOphistoryRequest", "history", Duration),
        ("DiscoveryRuleOverrideOperationOptrendRequest", "trends", Duration),
        ("DiscoveryRulePrototypeOverrideOperationOptrendRequest", "trends", Duration),
        ("HostInterfaceRequest", "port", Port),
        ("HostMassAddInterfaceRequest", "port", Port),
        ("HostMassInterfaceRequest", "port", Port),
        ("HostPrototypeInterfaceRequest", "port", Port),
        ("HostInterfaceMassAddInterfaceRequest", "port", Port),
        ("HostInterfaceMassRemoveInterfaceRequest", "port", Port),
        ("HostInterfaceReplaceHostInterfacesInterfaceRequest", "port", Port),
    ];

    // A select option whose name doesn't spell the member of the read record it fills: a graph's items are its gitems.
    private static readonly (string Object, string Select, string Member)[] s_selects =
    [
        ("graph", "selectGraphItems", "gitems"),
        ("graphprototype", "selectGraphItems", "gitems"),
    ];

    /// <summary><paramref name="api"/> with each field of a typed grammar given its kind.</summary>
    /// <param name="api">The description, its records not yet renamed.</param>
    public static ApiDescription Apply(ApiDescription api)
    {
        if (api is null) throw new ArgumentNullException(nameof(api));
        var types = new Dictionary<string, TypeDescription>(StringComparer.Ordinal);
        foreach (TypeDescription type in api.Types) types[type.Name] = type;
        return api with { Types = [.. api.Types.Select(type => type with { Fields = [.. type.Fields.Select(field => field with { Kind = KindOf(api, types, type, field) })] })] };
    }

    /// <summary>The record a get returns: its object's model, else the record its method answers.</summary>
    /// <param name="api">The description.</param>
    /// <param name="types">Its records by name.</param>
    /// <param name="get">A get's parameters.</param>
    internal static TypeDescription? RecordOf(ApiDescription api, IReadOnlyDictionary<string, TypeDescription> types, TypeDescription get)
    {
        if (get.Object is null || api.Services.FirstOrDefault(s => s.Object == get.Object) is not { } service) return null;
        if (service.Model is { } model && types.TryGetValue(model, out TypeDescription? read)) return read;
        string? result = service.Methods.FirstOrDefault(m => m.Parameters == "object:" + get.Name)?.Result;
        string? record = result is null ? null : result.StartsWith("list:object:", StringComparison.Ordinal) ? result.Substring(12) : result.StartsWith("object:", StringComparison.Ordinal) ? result.Substring(7) : null;
        return record is not null && types.TryGetValue(record, out TypeDescription? answered) ? answered : null;
    }

    private static string KindOf(ApiDescription api, IReadOnlyDictionary<string, TypeDescription> types, TypeDescription type, FieldDescription field)
    {
        if (field.Kind == "output") return Output + OutputTarget(api, types, type, field);
        if (type.Role == "get" && field.Wire == "sortfield" && field.Kind == "list:string")
            return SortFields + (RecordOf(api, types, type) is { } record ? "object:" + record.Name : "json");
        if (field.Kind != "string") return field.Kind;
        foreach ((string obj, string wire, string kind) in s_objectFields)
            if (type.Object == obj && field.Wire == wire && type.Role != "get") return kind;
        foreach ((string record, string wire, string kind) in s_nestedFields)
            if (type.Name == record && field.Wire == wire) return kind;
        return field.Kind;
    }

    // The record an output or select option names the fields of: a get's output names its record's, a select the record
    // the member it fills holds, found by the select's name without "select", case and underscores aside
    // (selectHostGroups fills hostgroups, select_acknowledges acknowledges). JSON where that's a value, not a record: a
    // role's rules, an event's related object, whose kind its source decides, an image's data, a diagnostic's statistics.
    private static string OutputTarget(ApiDescription api, IReadOnlyDictionary<string, TypeDescription> types, TypeDescription type, FieldDescription field)
    {
        if (type.Role != "get" || RecordOf(api, types, type) is not { } record) return "json";
        if (field.Wire == "output") return "object:" + record.Name;
        if (!field.Wire.StartsWith("select", StringComparison.Ordinal)) return "json";
        string member = s_selects.FirstOrDefault(s => s.Object == type.Object && s.Select == field.Wire).Member ?? Plain(field.Wire.Substring(6));
        FieldDescription[] filled = [.. record.Fields.Where(f => Plain(f.Wire) == Plain(member))];
        if (filled.Length != 1) return "json";
        string kind = filled[0].Kind.StartsWith("list:", StringComparison.Ordinal) ? filled[0].Kind.Substring(5) : filled[0].Kind;
        return kind.StartsWith("object:", StringComparison.Ordinal) && types.ContainsKey(kind.Substring(7)) ? kind : "json";
    }

    // A name without its underscores, in lower case, for matching a select to the member it fills.
    private static string Plain(string name) => new string(name.Where(c => c != '_').Select(char.ToLowerInvariant).ToArray());
}
