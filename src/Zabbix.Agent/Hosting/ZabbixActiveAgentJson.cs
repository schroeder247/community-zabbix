// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;
using Community.Zabbix.Protocol;

namespace Community.Zabbix.Hosting;

/// <summary>The active agent protocol's JSON context.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NumberHandling = JsonNumberHandling.AllowReadingFromString, Converters = [typeof(ZabbixIdNumberConverter)])]
[JsonSerializable(typeof(ZabbixActiveChecksRequest))]
[JsonSerializable(typeof(ZabbixActiveChecksResponse))]
[JsonSerializable(typeof(ZabbixAgentDataRequest))]
[JsonSerializable(typeof(ZabbixHeartbeatRequest))]
[JsonSerializable(typeof(ZabbixAgentResponse))]
internal sealed partial class ZabbixActiveAgentJson : JsonSerializerContext
{
}
