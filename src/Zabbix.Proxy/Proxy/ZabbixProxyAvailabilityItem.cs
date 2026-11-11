// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Proxy;

/// <summary>
/// One row of <c>interface availability</c> (wire tag; despite the name this carries the classic-agent interface's
/// state, not a host — <c>process_interfaces_availability_contents</c>). Upstream's own field is <c>available</c>/
/// <c>error</c> for the agent check alone: there is no <c>errors_from</c>/<c>disable_until</c> on this wire (measured on
/// 7.0.31, 7.4.15 and 8.0.0rc1 — those are DB columns the server derives, never sent).
/// </summary>
/// <param name="InterfaceId">The interface.</param>
/// <param name="Available">0 unknown, 1 available, 2 not available (<c>ZBX_INTERFACE_AVAILABLE_*</c>); omitted to leave it as the server has it.</param>
/// <param name="Error">The last poll's error, when <paramref name="Available"/> is not available.</param>
public sealed record ZabbixProxyAvailabilityItem(
    [property: JsonPropertyName("interfaceid")] ulong InterfaceId,
    [property: JsonPropertyName("available")] int? Available = null,
    [property: JsonPropertyName("error")] string? Error = null);
