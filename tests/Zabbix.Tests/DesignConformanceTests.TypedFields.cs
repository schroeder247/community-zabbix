// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Text.Json.Serialization;
using Community.Zabbix.Tests.Conformance;

namespace Community.Zabbix.Tests;

/// <summary>
/// Typed where Zabbix's grammar is known (ADR 0013): every public member that carries an update interval
/// (<c>delay</c>) is a <see cref="ZabbixInterval"/>, a length of an item's history, trends or timeout a
/// <see cref="ZabbixDuration"/>, an interface's <c>port</c> a <see cref="ZabbixPort"/>, the allowed hosts
/// (<c>trapper_hosts</c>) a list of <see cref="ZabbixPeer"/>; a get's <c>output</c> and every <c>select</c> option a
/// <see cref="ZabbixOutput{TRecord}"/>, and its sort one list of <see cref="ZabbixSort{TRecord}"/>, of the record the get
/// returns, with no public <c>sortorder</c> beside it. The members the table below names carry another grammar, and say
/// which.
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
                "TypedFormProbe.History (history): String",
                "TypedFormProbe.Output (output): String",
                "TypedFormProbe.Port (port): Int32",
                "TypedFormProbe.SelectHosts (selectHosts): String",
                "TypedFormProbe.SortField (sortfield): IReadOnlyList`1",
                "TypedFormProbe.SortOrder (sortorder): a public sortorder beside the sort",
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
                    "delay" => bare == typeof(ZabbixInterval),
                    "history" or "trends" or "timeout" => bare == typeof(ZabbixDuration),
                    "port" => bare == typeof(ZabbixPort),
                    "trapper_hosts" => bare == typeof(IReadOnlyList<ZabbixPeer>),
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

    // ZabbixOutput<TRecord>, of the record the get returns when the get says which.
    private static bool IsOutput(Type type, Type? queried)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ZabbixOutput<>) && (queried is null || type.GetGenericArguments()[0] == queried);

    // The members whose wire name is one of the typed grammars' but whose value is another's: what it is instead.
    private static string? OtherGrammar(string type, string wire) => (type, wire) switch
    {
        ("ZabbixHttpTest" or "ZabbixHttpTestCreateRequest" or "ZabbixHttpTestUpdateRequest", "delay") => "a web scenario's interval: a length (time unit or user macro), no flexible or scheduling part",
        ("ZabbixNetworkDiscoveryRule" or "ZabbixNetworkDiscoveryRuleCreateRequest" or "ZabbixNetworkDiscoveryRuleUpdateRequest", "delay") => "a network discovery rule's interval: a length, no flexible or scheduling part",
        ("ZabbixHistoryGetParameters", "history") => "the value type history.get reads, not a length",
        ("ZabbixConnector" or "ZabbixConnectorCreateRequest" or "ZabbixConnectorUpdateRequest", "timeout") => "a connector's timeout: a length with its own range, outside the item fields this step types",
        ("ZabbixHttpTestStep" or "ZabbixHttpTestStepRequest" or "ZabbixHttpTestUpdateStepRequest", "timeout") => "a web scenario step's timeout: a length outside the item fields this step types",
        ("ZabbixMediaType" or "ZabbixMediaTypeCreateRequest" or "ZabbixMediaTypeUpdateRequest", "timeout") => "a webhook's timeout: a length outside the item fields this step types",
        ("ZabbixScript" or "ZabbixScriptCreateRequest" or "ZabbixScriptUpdateRequest", "timeout") => "a script's timeout: a length outside the item fields this step types",
        ("ZabbixProxy" or "ZabbixProxyCreateRequest" or "ZabbixProxyUpdateRequest", "port") => "a proxy's own port, not an interface's",
        ("ZabbixScript" or "ZabbixScriptCreateRequest" or "ZabbixScriptUpdateRequest", "port") => "an SSH or Telnet script's port, not an interface's",
        ("ZabbixUserDirectory" or "ZabbixUserDirectoryCreateRequest" or "ZabbixUserDirectoryUpdateRequest" or "ZabbixUserDirectoryTestRequest", "port") => "an LDAP server's port, not an interface's",
        ("ZabbixDiscoveredService", "port") => "the port discovery found a service on: a number the server writes",
        ("ZabbixHaNode", "port") => "a server node's port: a number the server writes",
        ("ZabbixProxyAutoregistrationItem" or "ZabbixProxyDiscoveryItem" or "ZabbixProxyTask", "port") => "the proxy protocol's port: a number a proxy writes",
        ("ZabbixServiceGetParameters", "selectStatusTimeline") => "a list of periods to report on, not an output",
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
    }
}
