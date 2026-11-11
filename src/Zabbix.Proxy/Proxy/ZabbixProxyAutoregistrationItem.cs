// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Proxy;

/// <summary>One row of <c>auto registration</c>: an unknown active-agent host offered for registration (<c>process_auto_registration_contents</c>).</summary>
/// <param name="Clock">When the agent was seen.</param>
/// <param name="HostName">The host name it announced.</param>
/// <param name="Ip">Its address; empty when <paramref name="Flags"/> is DNS-only.</param>
/// <param name="Dns">Its name, when resolved.</param>
/// <param name="Port">Its agent port; the default (10050) when omitted.</param>
/// <param name="HostMetadata">Its <c>HostMetadata</c>, when the agent sends one.</param>
/// <param name="Flags">0 use both, 1 IP only, 2 DNS only (<c>ZBX_CONN_DEFAULT</c>/<c>IP</c>/<c>DNS</c>).</param>
/// <param name="TlsAccepted">The connection type it was seen on: 1 unencrypted, 2 PSK, 4 certificate.</param>
public sealed record ZabbixProxyAutoregistrationItem(
    [property: JsonPropertyName("clock"), JsonConverter(typeof(ZabbixUnixTimeConverter))] DateTimeOffset Clock,
    [property: JsonPropertyName("host")] string HostName,
    [property: JsonPropertyName("ip")] string? Ip,
    [property: JsonPropertyName("dns")] string? Dns,
    [property: JsonPropertyName("port")] int? Port,
    [property: JsonPropertyName("host_metadata")] string? HostMetadata = null,
    [property: JsonPropertyName("flags")] int? Flags = null,
    [property: JsonPropertyName("tls_accepted")] int? TlsAccepted = null);
