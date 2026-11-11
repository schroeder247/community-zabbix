// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Listening;

/// <summary>The answer to a JSON request that is not a readable passive check: one error, no data (listener.c <c>fail:</c>).</summary>
internal sealed record ZabbixPassiveCheckRefusal(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("variant")] int Variant,
    [property: JsonPropertyName("error")] string Error);
