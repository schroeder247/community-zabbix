// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Protocol;

/// <summary>The trapper wire's JSON contract, generated: what the sender writes and reads, trim- and AOT-safe.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SenderRequest))]
[JsonSerializable(typeof(ZabbixSenderItem))]
[JsonSerializable(typeof(RawSenderResponse))]
internal sealed partial class ZabbixWireJson : JsonSerializerContext
{
}
