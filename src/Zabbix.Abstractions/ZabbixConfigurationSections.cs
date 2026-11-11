// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Names the sections of the <c>Zabbix</c> configuration document: servers, push agents, active agents, passive agents and
/// proxies, each a map from name to its options, so declaring a name twice continues configuring that row. A fluent chain
/// (<c>AddZabbixClient("prod")</c> and its verbs), a configuration file (JSON, or any other <c>IConfiguration</c> source) and a hashtable
/// all bind to the same named options through the same verbs, so the forms cannot drift.
/// </summary>
/// <remarks>
/// <para>
/// Each package binds only the sections it builds, from the same <see cref="Section"/>: <c>Zabbix.Sdk</c>'s
/// <c>AddZabbixClients</c> binds <see cref="Servers"/>; <c>Zabbix.Agent</c>'s <c>AddZabbixAgents</c> binds
/// <see cref="PushAgents"/> and <see cref="ActiveAgents"/>; <c>Zabbix.Proxy</c>'s <c>AddZabbixProxies</c> binds
/// <see cref="PushAgents"/>, each agent's <see cref="Trapper"/>, <see cref="PassiveAgents"/> and <see cref="Proxies"/>. A
/// document written for the wider graph still binds through a narrower root, leaving the other sections unregistered,
/// and a host that also calls the API calls <c>AddZabbixClients</c> beside its hosting root.
/// </para>
/// <para>
/// The roots are hand-written, not generated, because the sections are not uniform: a server row also builds an
/// <c>HttpClient</c> pipeline and a keyed credential, a proxy row picks an active or a passive proxy by its <c>Mode</c>, and a
/// trapper is a conditional subsection of a push agent row.
/// </para>
/// </remarks>
/// <example>
/// <code language="json">
/// {
///   "Zabbix": {
///     "Servers": {
///       "prod": { "Endpoint": "https://zbx.example/api_jsonrpc.php", "ApiToken": "...", "Resilience": { "Retry": { "MaxRetryAttempts": 5 } } }
///     },
///     "PushAgents": {
///       "edge-01": {
///         "ServerActive": "zbx-server-01;zbx-server-02:10052",
///         "MaxItems": 10000,
///         "Send": { "BatchSize": 250, "FlushInterval": "00:00:05" },
///         "Connection": { "Tls": { "Mode": "PreSharedKey", "PskIdentity": "edge", "Psk": "..." } },
///         "Trapper": { "Port": 10051, "AllowedPeers": [ "10.0.0.0/8" ] }
///       }
///     },
///     "ActiveAgents": { "roaming-01": { "ServerActive": "zbx-server-01" } },
///     "PassiveAgents": { "probe-01": { "AllowedPeers": [ "10.0.0.0/8" ] } },
///     "Proxies": { "proxy-01": { "Mode": "Passive", "Version": "7.0", "Trapper": { "AllowedPeers": [ "10.0.0.0/8" ] } } }
///   }
/// }
/// </code>
/// <c>Servers</c> is bound by <c>Zabbix.Sdk</c>'s <c>AddZabbixClients</c>; <c>PushAgents</c> by <c>Zabbix.Agent</c>'s
/// <c>AddZabbixAgents</c> and <c>Zabbix.Proxy</c>'s <c>AddZabbixProxies</c>; <c>ActiveAgents</c> by
/// <c>AddZabbixAgents</c> only; <c>PassiveAgents</c>, <c>Proxies</c> and an agent's <c>Trapper</c> by
/// <c>Zabbix.Proxy</c>'s <c>AddZabbixProxies</c> only. In <c>ServerActive</c>, <c>;</c> separates the HA nodes of
/// one destination and <c>,</c> one destination from another; a proxy's <c>Mode</c> is <c>Active</c> by default.
/// </example>
public static class ZabbixConfigurationSections
{
    /// <summary>The section a host's configuration keeps the document under.</summary>
    public const string Section = "Zabbix";

    /// <summary>The key of the servers map; bound by <c>Zabbix.Sdk</c>'s <c>AddZabbixClients</c>.</summary>
    public const string Servers = "Servers";

    /// <summary>The key of the push agents map; bound by <c>Zabbix.Agent</c>'s <c>AddZabbixAgents</c> and <c>Zabbix.Proxy</c>'s <c>AddZabbixProxies</c>.</summary>
    public const string PushAgents = "PushAgents";

    /// <summary>The key of a push agent's nested trapper subsection; bound only by <c>Zabbix.Proxy</c>'s <c>AddZabbixProxies</c>.</summary>
    public const string Trapper = "Trapper";

    /// <summary>The key of the active agents map; bound only by <c>Zabbix.Agent</c>'s <c>AddZabbixAgents</c>.</summary>
    public const string ActiveAgents = "ActiveAgents";

    /// <summary>The key of the passive agents map; bound only by <c>Zabbix.Proxy</c>'s <c>AddZabbixProxies</c>.</summary>
    public const string PassiveAgents = "PassiveAgents";

    /// <summary>The key of the proxies map; bound only by <c>Zabbix.Proxy</c>'s <c>AddZabbixProxies</c>.</summary>
    public const string Proxies = "Proxies";

    /// <summary>
    /// The keys of every map, by which each root tells the document from the host's whole configuration passed in its
    /// place: a configuration that holds none of them but a <see cref="Section"/> is read from that section.
    /// </summary>
    internal static IReadOnlyList<string> Maps { get; } = [Servers, PushAgents, ActiveAgents, PassiveAgents, Proxies];
}
