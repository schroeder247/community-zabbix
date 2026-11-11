// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Connector;

/// <summary>The connector's JSON context.</summary>
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString, Converters = [typeof(ZabbixConnectorRecordConverter)])]
[JsonSerializable(typeof(ZabbixConnectorRecord))]
[JsonSerializable(typeof(ZabbixConnectorValue))]
[JsonSerializable(typeof(ZabbixConnectorEvent))]
[JsonSerializable(typeof(ZabbixConnectorError))]
internal sealed partial class ZabbixConnectorJson : JsonSerializerContext
{
}
