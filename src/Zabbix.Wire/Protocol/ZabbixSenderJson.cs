// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Protocol;

/// <summary>The sender's JSON contract, generated: what it writes to a trapper and reads back, trim- and AOT-safe.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SenderRequest))]
[JsonSerializable(typeof(ZabbixSenderItem))]
[JsonSerializable(typeof(RawSenderResponse))]
internal sealed partial class ZabbixSenderJson : JsonSerializerContext
{
}
