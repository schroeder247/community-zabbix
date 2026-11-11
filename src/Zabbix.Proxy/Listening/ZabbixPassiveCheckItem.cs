// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Listening;

/// <summary>One key in a passive check request.</summary>
internal sealed record ZabbixPassiveCheckItem(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("timeout")] int? Timeout);
