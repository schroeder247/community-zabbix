// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.Generator;

/// <summary>
/// One method: its wire name, its C# name, its shape (<c>get-list</c>, <c>get-object</c>, <c>ids</c>, <c>call</c>), what
/// it takes and returns as kinds, and what the source says about it (versions, minimum user type, whether it reads,
/// whether it needs a session).
/// </summary>
internal sealed record MethodDescription(
    string Api,
    string Name,
    string Shape,
    string Parameters,
    string Result,
    bool Batch,
    bool Count,
    string? Since,
    string? Removed,
    string? Replacement,
    int? MinUserType,
    bool IsRead,
    bool Idempotent,
    bool RequiresAuthentication,
    string? AcceptsAuthenticationSince,
    string? Emulation,
    string? Source)
{
    public static MethodDescription Read(JsonElement element) => new(
        element.GetProperty("api").GetString()!,
        element.GetProperty("name").GetString()!,
        element.GetProperty("shape").GetString()!,
        element.GetProperty("parameters").GetString()!,
        element.GetProperty("result").GetString()!,
        ApiDescription.Flag(element, "batch"),
        ApiDescription.Flag(element, "count"),
        ApiDescription.Text(element, "since"),
        ApiDescription.Text(element, "removed"),
        ApiDescription.Text(element, "replacement"),
        element.TryGetProperty("minUserType", out JsonElement min) && min.ValueKind == JsonValueKind.Number ? min.GetInt32() : null,
        ApiDescription.Flag(element, "read"),
        ApiDescription.Flag(element, "idempotent"),
        !element.TryGetProperty("requiresAuthentication", out JsonElement auth) || auth.ValueKind != JsonValueKind.False,
        ApiDescription.Text(element, "acceptsAuthenticationSince"),
        ApiDescription.Text(element, "emulation"),
        ApiDescription.Text(element, "source"));
}
