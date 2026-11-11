// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Listening;

/// <summary>One answered key.</summary>
internal sealed record ZabbixPassiveCheckResult(
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("error")] string? Error);
