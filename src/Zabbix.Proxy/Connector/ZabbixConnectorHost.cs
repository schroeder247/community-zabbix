// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Connector;

/// <summary>A host as a connector line names it.</summary>
public sealed record ZabbixConnectorHost(
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("name")] string? Name);
