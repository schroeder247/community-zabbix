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
/// <item><see cref="Duration"/>: a length of time, a time unit (<c>API_TIME_UNIT</c>), <c>ZabbixDuration</c>.</item>
/// <item><see cref="Port"/>: a port, <c>ZabbixPort</c>.</item>
/// <item><see cref="Peers"/>: the hosts that may send to a trapper, a list of <c>ZabbixPeer</c>.</item>
/// <item><see cref="ItemKey"/>: an item key, <c>ZabbixItemKey</c>.</item>
/// <item><c>output:</c> and <c>list:field:</c>: a get's <c>output</c>, its <c>select</c> options and its <c>sortfield</c>,
/// typed by the record whose fields they name (<c>output:object:Host</c>), or <c>json</c> where the SDK reads that
/// object as JSON.</item>
/// <item><see cref="IdMap"/>: a method's reply keyed by the ids it was asked about (<c>script.getscriptsbyhosts</c>).</item>
/// </list>
/// </summary>
internal static class ZabbixTypedFields
{
    /// <summary>An update interval, in the grammar the API checks a delay against (<c>API_ITEM_DELAY</c>).</summary>
    public const string Interval = "interval";

    /// <summary>A length of time, the first part of an update interval: a number with a suffix, or a macro (<c>API_TIME_UNIT</c>).</summary>
    public const string Duration = "duration";

    /// <summary>A port: a number, or a macro where Zabbix takes one (<c>API_PORT</c>).</summary>
    public const string Port = "port";

    /// <summary>An item key: its name and parameters, as the item's <c>key_</c> holds them.</summary>
    public const string ItemKey = "itemkey";

    /// <summary>A reply keyed by the ids the request named, before the kind of each entry: <c>idmap:list:object:Script</c>.</summary>
    public const string IdMap = "idmap:";

    /// <summary>The hosts that may send to a trapper, as Zabbix's comma-separated list (<c>API_IP_RANGES</c>).</summary>
    public const string Peers = "list:peer";

    /// <summary>A get's <c>output</c> or <c>select</c> option, before the record whose fields it names.</summary>
    public const string Output = "output:";

    /// <summary>A get's <c>sortfield</c>, before the record whose fields it names.</summary>
    public const string SortFields = "list:field:";

    // The fields of an object's records, its read record and its requests, by the object and the field's wire name. An
    // item's, an item prototype's and a discovery rule's delay, timeout and trapper hosts are checked by one rule each
    // (CItemType.php 7.0:160-204), an item's history and trends as lengths of time (CItem.php 7.0:546,
    // CItemPrototype.php 7.0:395-397), and an interface's port as a number or a macro (CHostInterface.php 7.0:826-831).
    // Every other field the API checks as API_TIME_UNIT is a length of time, as each object's rules state it: an
    // action's escalation period (CAction.php 7.0:417), the LDAP provisioning interval (CAuthentication.php), a
    // connector's and a media type's attempt interval and timeout (CConnector.php, CMediatype.php), a discovery rule's
    // lifetimes (CDiscoveryRule.php), the housekeeper's periods (CHousekeeping.php), a web scenario's and a network
    // discovery rule's interval (CHttpTest.php, CDRule.php 7.0:236, which takes a length and no flexible interval), an
    // 8.0 item's aggregation window (CItem.php 8.0), a proxy's and the settings' timeouts (CProxy.php, CSettings.php),
    // a proxy group's failover delay (CProxyGroup.php), a script's timeout (CScript.php) and a user's session and
    // refresh periods (CUser.php); the settings' failover delay, which the API only reads, is one too. A proxy's, a
    // script's and a user directory's port is API_PORT (CProxy.php, CScript.php, CUserDirectory.php), and a media
    // type's SMTP port, a discovered service's and an HA node's are ports too, though the API checks or writes them as
    // numbers. An item key is an item's, a prototype's or a discovery rule's key_, and the key history.push names an
    // item by.
    private static readonly (string Object, string Wire, string Kind)[] s_objectFields =
    [
        ("item", "delay", Interval),
        ("itemprototype", "delay", Interval),
        ("discoveryrule", "delay", Interval),
        ("discoveryruleprototype", "delay", Interval),
        ("item", "history", Duration),
        ("item", "trends", Duration),
        ("item", "timeout", Duration),
        ("item", "granularity", Duration),
        ("item", "lookback_limit", Duration),
        ("item", "time_shift", Duration),
        ("itemprototype", "history", Duration),
        ("itemprototype", "trends", Duration),
        ("itemprototype", "timeout", Duration),
        ("itemprototype", "granularity", Duration),
        ("itemprototype", "lookback_limit", Duration),
        ("itemprototype", "time_shift", Duration),
        ("discoveryrule", "timeout", Duration),
        ("discoveryrule", "lifetime", Duration),
        ("discoveryrule", "enabled_lifetime", Duration),
        ("discoveryruleprototype", "timeout", Duration),
        ("discoveryruleprototype", "lifetime", Duration),
        ("discoveryruleprototype", "enabled_lifetime", Duration),
        ("action", "esc_period", Duration),
        ("authentication", "jit_provision_interval", Duration),
        ("connector", "attempt_interval", Duration),
        ("connector", "timeout", Duration),
        ("drule", "delay", Duration),
        ("housekeeping", "compress_older", Duration),
        ("housekeeping", "hk_audit", Duration),
        ("housekeeping", "hk_events_autoreg", Duration),
        ("housekeeping", "hk_events_discovery", Duration),
        ("housekeeping", "hk_events_internal", Duration),
        ("housekeeping", "hk_events_service", Duration),
        ("housekeeping", "hk_events_trigger", Duration),
        ("housekeeping", "hk_history", Duration),
        ("housekeeping", "hk_services", Duration),
        ("housekeeping", "hk_sessions", Duration),
        ("housekeeping", "hk_trends", Duration),
        ("httptest", "delay", Duration),
        ("mediatype", "attempt_interval", Duration),
        ("mediatype", "timeout", Duration),
        ("proxy", "timeout_browser", Duration),
        ("proxy", "timeout_db_monitor", Duration),
        ("proxy", "timeout_external_check", Duration),
        ("proxy", "timeout_http_agent", Duration),
        ("proxy", "timeout_script", Duration),
        ("proxy", "timeout_simple_check", Duration),
        ("proxy", "timeout_snmp_agent", Duration),
        ("proxy", "timeout_ssh_agent", Duration),
        ("proxy", "timeout_telemetry_query", Duration),
        ("proxy", "timeout_telnet_agent", Duration),
        ("proxy", "timeout_zabbix_agent", Duration),
        ("proxygroup", "failover_delay", Duration),
        ("script", "timeout", Duration),
        ("settings", "blink_period", Duration),
        ("settings", "connect_timeout", Duration),
        ("settings", "device_link_timeout", Duration),
        ("settings", "ha_failover_delay", Duration),
        ("settings", "history_period", Duration),
        ("settings", "item_test_timeout", Duration),
        ("settings", "login_block", Duration),
        ("settings", "max_period", Duration),
        ("settings", "media_type_test_timeout", Duration),
        ("settings", "ok_period", Duration),
        ("settings", "period_default", Duration),
        ("settings", "report_test_timeout", Duration),
        ("settings", "script_timeout", Duration),
        ("settings", "socket_timeout", Duration),
        ("settings", "timeout_browser", Duration),
        ("settings", "timeout_db_monitor", Duration),
        ("settings", "timeout_external_check", Duration),
        ("settings", "timeout_http_agent", Duration),
        ("settings", "timeout_script", Duration),
        ("settings", "timeout_simple_check", Duration),
        ("settings", "timeout_snmp_agent", Duration),
        ("settings", "timeout_ssh_agent", Duration),
        ("settings", "timeout_telemetry_query", Duration),
        ("settings", "timeout_telnet_agent", Duration),
        ("settings", "timeout_zabbix_agent", Duration),
        ("user", "autologout", Duration),
        ("user", "refresh", Duration),
        ("user", "default_maintenance_period", Duration),
        ("item", "trapper_hosts", Peers),
        ("itemprototype", "trapper_hosts", Peers),
        ("discoveryrule", "trapper_hosts", Peers),
        ("discoveryruleprototype", "trapper_hosts", Peers),
        ("hostinterface", "port", Port),
        ("proxy", "port", Port),
        ("proxy", "local_port", Port),
        ("script", "port", Port),
        ("userdirectory", "port", Port),
        ("mediatype", "smtp_port", Port),
        ("dservice", "port", Port),
        ("hanode", "port", Port),
        ("item", "key_", ItemKey),
        ("itemprototype", "key_", ItemKey),
        ("discoveryrule", "key_", ItemKey),
        ("discoveryruleprototype", "key_", ItemKey),
        ("history", "key", ItemKey),
    ];

    // The fields of records nested in a request or a reply, which name no object, by the record and the field's wire
    // name: the interval, the history and the trends an override of a discovery rule sets (CDiscoveryRule.php 7.0:1257),
    // the port of each interface a host's, a host prototype's (CHostPrototype.php 7.0:740) and the interfaces' own
    // requests hold, an action operation's escalation period (CAction.php 7.0), a web scenario step's timeout
    // (CHttpTest.php), a maintenance period's length (CMaintenance.php), an 8.0 correlation rule's window and suppression
    // (CCepRule.php 8.0), the escalation period a script's action reports, the key of the prototype an item was
    // discovered from, and the lengths of time of the user a session authenticates (user.checkAuthentication).
    private static readonly (string Record, string Wire, string Kind)[] s_nestedFields =
    [
        ("DiscoveryRuleOverrideOperationOpperiodRequest", "delay", Interval),
        ("DiscoveryRulePrototypeOverrideOperationOpperiodRequest", "delay", Interval),
        ("DiscoveryRuleOverrideOperationOphistoryRequest", "history", Duration),
        ("DiscoveryRulePrototypeOverrideOperationOphistoryRequest", "history", Duration),
        ("DiscoveryRuleOverrideOperationOptrendRequest", "trends", Duration),
        ("DiscoveryRulePrototypeOverrideOperationOptrendRequest", "trends", Duration),
        ("ActionOperationRequest", "esc_period", Duration),
        ("ActionReadOperation", "esc_period", Duration),
        ("ScriptAction", "esc_period", Duration),
        ("HttpTestStep", "timeout", Duration),
        ("HttpTestStepRequest", "timeout", Duration),
        ("HttpTestUpdateStepRequest", "timeout", Duration),
        ("MaintenanceTimeperiodRequest", "period", Duration),
        ("MaintenanceRecordTimeperiod", "period", Duration),
        ("CepRuleWindow", "duration", Duration),
        ("CepRuleWindowRequest", "duration", Duration),
        ("CepRuleOperation", "suppress_duration", Duration),
        ("CepRuleOperationRequest", "suppress_duration", Duration),
        ("AuthenticatedUser", "autologout", Duration),
        ("AuthenticatedUser", "refresh", Duration),
        ("AuthenticatedUser", "default_maintenance_period", Duration),
        ("HostInterfaceRequest", "port", Port),
        ("HostMassAddInterfaceRequest", "port", Port),
        ("HostMassInterfaceRequest", "port", Port),
        ("HostPrototypeInterfaceRequest", "port", Port),
        ("HostInterfaceMassAddInterfaceRequest", "port", Port),
        ("HostInterfaceMassRemoveInterfaceRequest", "port", Port),
        ("HostInterfaceReplaceHostInterfacesInterfaceRequest", "port", Port),
        ("ItemItemDiscovery", "key_", ItemKey),
        ("ItemDiscoveryData", "key_", ItemKey),
    ];

    // The methods whose reply is keyed by the ids the request named: the scripts a user may run on each host, and on each
    // event (CScript.php 7.0: getScriptsByHosts and getScriptsByEvents key their result by host id and by event id).
    private static readonly HashSet<string> s_idKeyedReplies = new(StringComparer.Ordinal) { "script.getscriptsbyhosts", "script.getscriptsbyevents" };

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
        return api with
        {
            Types = [.. api.Types.Select(type => type with { Fields = [.. type.Fields.Select(field => field with { Kind = KindOf(api, types, type, field) })] })],
            Services = [.. api.Services.Select(service => service with { Methods = [.. service.Methods.Select(method => method with { Result = ReplyOf(method) })] })],
        };
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
        if (field.Kind is not ("string" or "int")) return field.Kind;
        string? typed = null;
        foreach ((string obj, string wire, string kind) in s_objectFields)
            if (type.Object == obj && field.Wire == wire && type.Role != "get") typed = kind;
        foreach ((string record, string wire, string kind) in s_nestedFields)
            if (type.Name == record && field.Wire == wire) typed = kind;
        // a port or a length the API writes as a number on a read record (a user directory's port, a maintenance period's
        // length) is the same port or length its request sends as text
        return typed is not null && (field.Kind == "string" || typed is Port or Duration) ? typed : field.Kind;
    }

    // A reply keyed by the ids its request named is keyed by those ids, not by their text.
    private static string ReplyOf(MethodDescription method)
        => s_idKeyedReplies.Contains(method.Api) && method.Result.StartsWith("map:", StringComparison.Ordinal) ? IdMap + method.Result.Substring(4) : method.Result;

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
