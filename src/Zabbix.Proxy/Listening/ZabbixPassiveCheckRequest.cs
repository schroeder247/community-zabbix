// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Listening;

/// <summary>A passive check as the server frames it (7.x): <c>{"request":"passive checks","data":[{"key":…,"timeout":…}]}</c>.</summary>
internal sealed record ZabbixPassiveCheckRequest(
    [property: JsonPropertyName("request")] string? Request,
    [property: JsonPropertyName("data")] IReadOnlyList<ZabbixPassiveCheckItem>? Data);
