// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Protocol;

/// <summary>The trapper's <c>sender data</c> request envelope.</summary>
/// <param name="Request">Always <c>sender data</c>.</param>
/// <param name="Data">The values.</param>
internal sealed record SenderRequest(
    [property: JsonPropertyName("request")] string Request,
    [property: JsonPropertyName("data")] IReadOnlyCollection<ZabbixSenderItem> Data);
