// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;
using Community.Zabbix.Protocol;

namespace Community.Zabbix.Proxy;

/// <summary>The proxy protocol's JSON context.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NumberHandling = JsonNumberHandling.AllowReadingFromString, Converters = [typeof(ZabbixIdNumberConverter)])]
[JsonSerializable(typeof(ZabbixProxyConfigRequest))]
[JsonSerializable(typeof(ZabbixProxyConfigState))]
[JsonSerializable(typeof(ZabbixProxyDataRequest))]
[JsonSerializable(typeof(ZabbixProxyTasksAnswer))]
[JsonSerializable(typeof(ZabbixProxyResponse))]
[JsonSerializable(typeof(ZabbixProxyDiscoveryItem))]
[JsonSerializable(typeof(ZabbixProxyAutoregistrationItem))]
[JsonSerializable(typeof(ZabbixProxyAvailabilityItem))]
[JsonSerializable(typeof(ZabbixProxyTaskResult))]
[JsonSerializable(typeof(IReadOnlyList<ZabbixProxyDiscoveryItem>))]
[JsonSerializable(typeof(IReadOnlyList<ZabbixProxyAutoregistrationItem>))]
[JsonSerializable(typeof(IReadOnlyList<ZabbixProxyAvailabilityItem>))]
[JsonSerializable(typeof(IReadOnlyList<ZabbixProxyTaskResult>))]
[JsonSerializable(typeof(ZabbixProxyTask))]
[JsonSerializable(typeof(IReadOnlyList<ZabbixProxyTask>))]
internal sealed partial class ZabbixProxyJson : JsonSerializerContext
{
}
