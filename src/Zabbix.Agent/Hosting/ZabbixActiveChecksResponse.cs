// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Hosting;

/// <summary>The server's answer: the items, under a revision, and whether it takes history now (<c>upload</c>).</summary>
internal sealed record ZabbixActiveChecksResponse(
    [property: JsonPropertyName("response")] string? Response,
    [property: JsonPropertyName("data")] IReadOnlyList<ZabbixItemAssignment>? Data,
    [property: JsonPropertyName("config_revision")] long? ConfigRevision,
    [property: JsonPropertyName("info")] string? Info,
    [property: JsonPropertyName("upload")] string? Upload = null);
