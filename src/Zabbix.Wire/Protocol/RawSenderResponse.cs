// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Protocol;

/// <summary>The reply as the wire spells it, before its <c>info</c> line is parsed.</summary>
/// <param name="Response">The response word.</param>
/// <param name="Info">The info line.</param>
internal sealed record RawSenderResponse(
    [property: JsonPropertyName("response")] string? Response,
    [property: JsonPropertyName("info")] string? Info);
