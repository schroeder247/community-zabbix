// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>The login contract, generated.</summary>
[JsonSerializable(typeof(ZabbixLoginParameters))]
[JsonSerializable(typeof(ZabbixLoginUser))]
internal sealed partial class ZabbixLoginJson : JsonSerializerContext
{
}
