// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Listening;

/// <summary>The answer to a passive check: a value or an error per key.</summary>
internal sealed record ZabbixPassiveCheckResponse(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("variant")] int Variant,
    [property: JsonPropertyName("data")] IReadOnlyList<ZabbixPassiveCheckResult> Data);
