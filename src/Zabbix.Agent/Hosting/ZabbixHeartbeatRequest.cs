// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Hosting;

/// <summary><c>active check heartbeat</c>: "I am alive", every <c>heartbeat_freq</c> seconds.</summary>
internal sealed record ZabbixHeartbeatRequest(
    [property: JsonPropertyName("request")] string Request,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("heartbeat_freq")] int HeartbeatFreq,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("variant")] int Variant);
