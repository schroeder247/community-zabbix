// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Diagnostics;

/// <summary>
/// Provides the names under which the SDK publishes its telemetry, by layer. Every activity source and meter is named
/// under <see cref="Prefix"/>, so an application subscribes once with <c>AddSource(ZabbixTelemetryNames.Subscription)</c>
/// and <c>AddMeter(ZabbixTelemetryNames.Subscription)</c> and a layer added later is picked up without a new line (the
/// OpenTelemetry SDK matches the wildcard for nested names, as of 1.18.0). The library publishes only; the application
/// chooses collection and export.
/// </summary>
/// <remarks>
/// Log events are source-generated per layer (a <c>{Layer}Log</c> beside its layer), and each package owns one block of
/// ids: 1000–1999 the wire (<c>WireLog</c>: 10xx the sender, 11xx the exchange, 12xx the getter, 13xx the server wire);
/// 2000–3999 the agent package (<c>StoreLog</c> 20xx–21xx, <c>AgentLog</c> 30xx and up); 4000–5999 the API client
/// (<c>ApiLog</c> 40xx, <c>TreeLog</c> 50xx); 6000–7999 the proxy package (<c>ListenerLog</c> 60xx and up,
/// <c>ProxyLog</c> 70xx); 14000–14999 the PowerShell modules. An id never moves once released and is never reused.
/// </remarks>
public static class ZabbixTelemetryNames
{
    /// <summary>The root every name starts with.</summary>
    public const string Prefix = "Community.Zabbix.";

    /// <summary>The wildcard that subscribes to every layer: <c>Community.Zabbix.*</c>.</summary>
    public const string Subscription = "Community.Zabbix.*";

    /// <summary>The JSON-RPC API client (a span and a duration per call).</summary>
    public const string Api = "Community.Zabbix.Api";

    /// <summary>The framed-TCP exchanges: sender, getter, server wire, proxy uplink, agent traffic.</summary>
    public const string Wire = "Community.Zabbix.Wire";

    /// <summary>The listeners: trapper, passive agent, passive proxy.</summary>
    public const string Listener = "Community.Zabbix.Listener";

    /// <summary>The stores: drops, refusals and discards.</summary>
    public const string Store = "Community.Zabbix.Store";

    /// <summary>The pull plane: history pages and watcher ticks.</summary>
    public const string Tree = "Community.Zabbix.Tree";

    /// <summary>The agents: polls, deliveries, active-check refreshes.</summary>
    public const string Agent = "Community.Zabbix.Agent";

    /// <summary>The proxies: configuration pulls and uploads.</summary>
    public const string Proxy = "Community.Zabbix.Proxy";

    /// <summary>The PowerShell module: one span per command.</summary>
    public const string PowerShell = "Community.Zabbix.PowerShell";
}
