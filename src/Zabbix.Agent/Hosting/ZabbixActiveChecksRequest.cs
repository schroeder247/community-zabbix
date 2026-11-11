// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Hosting;

/// <summary><c>active checks</c>: the agent asks which items it should collect.</summary>
internal sealed record ZabbixActiveChecksRequest(
    [property: JsonPropertyName("request")] string Request,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("host_metadata")] string? HostMetadata,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("variant")] int Variant,
    [property: JsonPropertyName("config_revision")] long ConfigRevision,
    [property: JsonPropertyName("session")] string Session);
