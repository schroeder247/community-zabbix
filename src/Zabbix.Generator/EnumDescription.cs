// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.Generator;

/// <summary>A define family as an enum.</summary>
internal sealed record EnumDescription(string Name, string Family, string? Summary, bool Flags, EquatableArray<EnumMember> Members)
{
    public static EnumDescription Read(JsonElement element) => new(
        element.GetProperty("name").GetString()!,
        element.GetProperty("family").GetString()!,
        ApiDescription.Text(element, "summary"),
        ApiDescription.Flag(element, "flags"),
        [.. element.GetProperty("members").EnumerateArray().Select(m => new EnumMember(
            m.GetProperty("name").GetString()!,
            m.GetProperty("value").GetInt64(),
            ApiDescription.Text(m, "define"),
            ApiDescription.Text(m, "summary"),
            ApiDescription.Text(m, "since"),
            ApiDescription.Text(m, "removed")))]);
}
