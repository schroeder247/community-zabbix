// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Hosting;

/// <summary><c>agent data</c>: a batch of values.</summary>
internal sealed record ZabbixAgentDataRequest(
    [property: JsonPropertyName("request")] string Request,
    [property: JsonPropertyName("data")] IReadOnlyList<ZabbixAgentDataItem> Data,
    [property: JsonPropertyName("session")] string Session,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("variant")] int Variant);
