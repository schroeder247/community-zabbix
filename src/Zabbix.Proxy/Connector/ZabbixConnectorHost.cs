// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Connector;

/// <summary>A host as a connector line names it.</summary>
/// <param name="HostName">The host's technical name.</param>
/// <param name="Name">The host's visible name.</param>
public sealed record ZabbixConnectorHost(
    [property: JsonPropertyName("host")] string? HostName,
    [property: JsonPropertyName("name")] string? Name);
