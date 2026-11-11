// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Proxy;

/// <summary><c>proxy config</c>: what an active proxy asks the server, with the revisions it already holds.</summary>
internal sealed record ZabbixProxyConfigRequest(
    [property: JsonPropertyName("request")] string Request,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("session")] string Session,
    [property: JsonPropertyName("config_revision")] long ConfigRevision,
    [property: JsonPropertyName("hostmap_revision")] long? HostmapRevision);
