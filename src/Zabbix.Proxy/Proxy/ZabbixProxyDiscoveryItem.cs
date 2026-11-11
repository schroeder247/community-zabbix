// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Proxy;

/// <summary>
/// Represents one row of <c>discovery data</c>, as the wire carries it (<c>process_discovery_data_contents</c>,
/// <c>src/libs/zbxdbwrap/proxy.c</c>).
/// </summary>
/// <remarks>
/// The rows are a protocol, not independent records: for each address, its service rows, then one row with
/// <see cref="DcheckId"/> 0, port 0 and value "" that closes the host; the server processes the services only when that
/// row arrives. Most callers write a <see cref="ZabbixProxyDiscoveredHost"/>, which emits exactly these rows
/// (<see cref="ZabbixProxyDiscovery.WriteAsync(System.Threading.Channels.ChannelWriter{ZabbixProxyDiscoveryItem}, ZabbixProxyDiscoveredHost, CancellationToken)"/>).
/// </remarks>
/// <param name="Clock">When the check ran.</param>
/// <param name="DruleId">The discovery rule (wire tag <c>drule</c>).</param>
/// <param name="DcheckId">
/// The check within the rule (wire tag <c>dcheck</c>); 0 for the row that closes a host and for a finalized rule's row, as
/// upstream's proxy writes them. Always sent: every server refuses a whole <c>proxy data</c> message with a service row that
/// lacks it ("cannot find pair with name "dcheck"", on 7.0, 7.4 and 8.0).
/// </param>
/// <param name="Ip">The address checked.</param>
/// <param name="Dns">The name resolved, when the check does that.</param>
/// <param name="Port">The port checked.</param>
/// <param name="Value">What the check returned.</param>
/// <param name="Status">0 up, 1 down (<c>DOBJECT_STATUS_UP</c>/<c>DOWN</c>); 2 is a finalized rule (<c>DOBJECT_STATUS_FINALIZED</c>, <see cref="Error"/> instead of <see cref="Value"/>).</param>
/// <param name="Error">Why a finalized rule stopped, when it did.</param>
public sealed record ZabbixProxyDiscoveryItem(
    [property: JsonPropertyName("clock"), JsonConverter(typeof(ZabbixUnixTimeConverter))] DateTimeOffset Clock,
    [property: JsonPropertyName("drule")] ulong DruleId,
    [property: JsonPropertyName("dcheck")] ulong DcheckId,
    [property: JsonPropertyName("ip")] string Ip,
    [property: JsonPropertyName("dns")] string? Dns,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("error")] string? Error = null);
