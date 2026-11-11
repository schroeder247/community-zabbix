// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix.Tests;

/// <summary>A parameters object a test sends.</summary>
public sealed record OutputParameters([property: JsonPropertyName("output")] string Output);

/// <summary>A row a test reads back.</summary>
public sealed record HostRow([property: JsonPropertyName("hostid")] string HostId);

/// <summary>An object PHP may write as <c>[]</c> when it is empty.</summary>
public sealed record EmptyObjectRow([property: JsonPropertyName("host"), JsonConverter(typeof(ZabbixObjectOrEmptyArrayConverter<HostRow>))] HostRow? Host);

[JsonSerializable(typeof(OutputParameters))]
[JsonSerializable(typeof(List<HostRow>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(EmptyObjectRow))]
[JsonSerializable(typeof(DayOfWeek))]
internal sealed partial class TestJson : JsonSerializerContext
{
}
