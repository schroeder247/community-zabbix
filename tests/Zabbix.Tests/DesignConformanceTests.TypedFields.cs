// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Text.Json.Serialization;
using Community.Zabbix.Tests.Conformance;

namespace Community.Zabbix.Tests;

/// <summary>
/// Typed where Zabbix's grammar is known (ADR 0013, section 5): every public member that carries an update interval
/// (<c>delay</c>) is a <see cref="ZabbixInterval"/>, every length of time (an item's history, trends and timeout, a
/// discovery rule's lifetimes, an action's escalation period, the proxies' and the settings' timeouts, the housekeeper's
/// periods and the rest of the API's time units) a <see cref="ZabbixDuration"/>, every port a <see cref="ZabbixPort"/>,
/// every item key (<c>key_</c>) a <see cref="ZabbixItemKey"/>, the allowed hosts (<c>trapper_hosts</c>) a list of
/// <see cref="ZabbixPeer"/>; a get's <c>output</c> and every <c>select</c> option a <see cref="ZabbixOutput{TRecord}"/>,
/// and its sort one list of <see cref="ZabbixSort{TRecord}"/>, of the record the get returns, with no public
/// <c>sortorder</c> beside it. A request and the record it reads back agree, since the rule holds both. The members the
/// table below names carry another grammar, and say which.
/// </summary>
public sealed partial class DesignConformanceTests
{
    [Fact]
    public void Every_member_of_a_known_grammar_has_its_typed_form()
        => AssertNone(UntypedGrammarMembers(Libraries.PublicTypes));

    [Fact]
    public void The_typed_form_rule_sees_text_where_a_typed_form_belongs()
        => Assert.Equal(
            [
                "TypedFormProbe.Delay (delay): String",
                "TypedFormProbe.EscalationPeriod (esc_period): String",
                "TypedFormProbe.History (history): String",
                "TypedFormProbe.HousekeepingHistory (hk_history): String",
                "TypedFormProbe.Key (key_): String",
                "TypedFormProbe.Output (output): String",
                "TypedFormProbe.Port (port): Int32",
                "TypedFormProbe.ScriptTimeout (script_timeout): String",
                "TypedFormProbe.SelectHosts (selectHosts): String",
                "TypedFormProbe.SmtpPort (smtp_port): String",
                "TypedFormProbe.SortField (sortfield): IReadOnlyList`1",
                "TypedFormProbe.SortOrder (sortorder): a public sortorder beside the sort",
                "TypedFormProbe.TimeoutZabbixAgent (timeout_zabbix_agent): String",
                "TypedFormProbe.TrapperHosts (trapper_hosts): String",
            ],
            UntypedGrammarMembers([typeof(TypedFormProbe)]).Order(StringComparer.Ordinal));

    internal static IEnumerable<string> UntypedGrammarMembers(IEnumerable<Type> types)
    {
        foreach (Type type in types.Where(type => !IsCompilerGenerated(type)))
        {
            Type? queried = type.GetInterfaces().FirstOrDefault(face => face.IsGenericType && face.GetGenericTypeDefinition() == typeof(IZabbixGetParameters<>))?.GetGenericArguments()[0];
            List<(PropertyInfo Property, string Wire)> wired = [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(property => (property, property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name))
                .Where(pair => pair.Name is not null)
                .Select(pair => (pair.property, pair.Name!))];
            bool sorts = wired.Exists(pair => pair.Wire == "sortfield");
            bool gets = wired.Exists(pair => pair.Wire == "output");
            foreach ((PropertyInfo property, string wire) in wired)
            {
                Type bare = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                string where = $"{type.Name}.{property.Name} ({wire})";
                if (OtherGrammar(type.Name, wire) is not null) continue;
                bool typed = wire switch
                {
                    _ when TypedForm(type.Name, wire) is { } form => bare == form,
                    "output" => IsOutput(bare, queried),
                    "sortfield" => bare.IsGenericType && bare.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
                        && bare.GetGenericArguments()[0] is { IsGenericType: true } sort && sort.GetGenericTypeDefinition() == typeof(ZabbixSort<>)
                        && (queried is null || sort.GetGenericArguments()[0] == queried),
                    _ when gets && wire.StartsWith("select", StringComparison.Ordinal) => IsOutput(bare, null),
                    _ => true,
                };
                if (wire == "sortorder" && sorts) yield return $"{where}: a public sortorder beside the sort";
                else if (!typed) yield return $"{where}: {bare.Name}";
            }
        }
    }

    // The type a member of this wire name has, where the name says its grammar: a web scenario's and a network discovery
    // rule's interval is a length, with no flexible or scheduling part (CHttpTest.php, CDRule.php).
    private static Type? TypedForm(string type, string wire) => wire switch
    {
        "delay" when type.StartsWith("ZabbixHttpTest", StringComparison.Ordinal) || type.StartsWith("ZabbixNetworkDiscoveryRule", StringComparison.Ordinal) => typeof(ZabbixDuration),
        "delay" => typeof(ZabbixInterval),
        "port" or "local_port" or "smtp_port" => typeof(ZabbixPort),
        "key_" => typeof(ZabbixItemKey),
        "trapper_hosts" => typeof(IReadOnlyList<ZabbixPeer>),
        _ when IsLength(wire) => typeof(ZabbixDuration),
        _ => null,
    };

    // The wire names of the API's lengths of time (API_TIME_UNIT): the named ones, every timeout_* and *_timeout, and the
    // housekeeper's hk_* periods beside their *_mode and *_global switches.
    private static bool IsLength(string wire)
        => wire is "history" or "trends" or "timeout" or "lifetime" or "enabled_lifetime" or "esc_period" or "attempt_interval"
            or "failover_delay" or "ha_failover_delay" or "autologout" or "refresh" or "jit_provision_interval" or "compress_older"
            or "granularity" or "lookback_limit" or "time_shift" or "duration" or "suppress_duration" or "default_maintenance_period"
            or "blink_period" or "history_period" or "max_period" or "ok_period" or "period_default" or "login_block"
            || wire.StartsWith("timeout_", StringComparison.Ordinal)
            || wire.EndsWith("_timeout", StringComparison.Ordinal)
            || (wire.StartsWith("hk_", StringComparison.Ordinal) && !wire.EndsWith("_mode", StringComparison.Ordinal) && !wire.EndsWith("_global", StringComparison.Ordinal));

    // ZabbixOutput<TRecord>, of the record the get returns when the get says which.
    private static bool IsOutput(Type type, Type? queried)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ZabbixOutput<>) && (queried is null || type.GetGenericArguments()[0] == queried);

    // The members whose wire name is one of the typed grammars' but whose value is another's: what it is instead.
    private static string? OtherGrammar(string type, string wire) => (type, wire) switch
    {
        ("ZabbixHistoryGetParameters", "history") => "the value type history.get reads, not a length",
        ("ZabbixApmSpan" or "ZabbixApmTrace", "duration") => "a span's measured length: a number of nanoseconds the server writes",
        ("ZabbixNetworkDiscoveryCheck" or "ZabbixNetworkDiscoveryCheckRequest", "key_") => "a check's key: an agent item key, or the OID an SNMP check reads",
        ("ZabbixProxyAutoregistrationItem" or "ZabbixProxyDiscoveryItem" or "ZabbixProxyTask", "port") => "the proxy protocol's port: a number a proxy writes",
        ("ZabbixServiceGetParameters", "selectStatusTimeline") => "a list of periods to report on, not an output",
        ("ZabbixWriteCacheStatistics", "history") => "the part of zabbix.stats' write cache that holds the values, not a length",
        ("ZabbixItemTestConditions", "history") => "the previous value item.test's preprocessing compares with, not a length",
        _ => null,
    };

    private sealed class TypedFormProbe
    {
        [JsonPropertyName("delay")]
        public string? Delay { get; init; }

        [JsonPropertyName("history")]
        public string? History { get; init; }

        [JsonPropertyName("port")]
        public int? Port { get; init; }

        [JsonPropertyName("trapper_hosts")]
        public string? TrapperHosts { get; init; }

        [JsonPropertyName("output")]
        public string? Output { get; init; }

        [JsonPropertyName("selectHosts")]
        public string? SelectHosts { get; init; }

        [JsonPropertyName("sortfield")]
        public IReadOnlyList<string>? SortField { get; init; }

        [JsonPropertyName("sortorder")]
        public IReadOnlyList<string>? SortOrder { get; init; }

        [JsonPropertyName("delay_flex")]
        public string? DelayFlex { get; init; }

        [JsonPropertyName("esc_period")]
        public string? EscalationPeriod { get; init; }

        [JsonPropertyName("hk_history")]
        public string? HousekeepingHistory { get; init; }

        [JsonPropertyName("hk_history_mode")]
        public bool? HousekeepingHistoryMode { get; init; }

        [JsonPropertyName("key_")]
        public string? Key { get; init; }

        [JsonPropertyName("script_timeout")]
        public string? ScriptTimeout { get; init; }

        [JsonPropertyName("smtp_port")]
        public string? SmtpPort { get; init; }

        [JsonPropertyName("timeout_zabbix_agent")]
        public string? TimeoutZabbixAgent { get; init; }
    }
}
