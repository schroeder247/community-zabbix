// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.Generator;

/// <summary>
/// A record: a read model (<c>read</c>, what a <c>.get</c> returns), a get's parameters (<c>get</c>), a method's request
/// (<c>request</c>), or a record nested in one of those. <see cref="DescribedName"/> is the name the description and
/// <c>schema/overlay.json</c> key it by, kept when <see cref="ZabbixNames"/> renames it, so a diagnostic can point at the
/// overlay entry a human edits.
/// </summary>
internal sealed record TypeDescription(
    string Name,
    string Role,
    string? Summary,
    string? Object,
    string? Method,
    bool ExtensionData,
    bool GetParameters,
    EquatableArray<string> Filterable,
    EquatableArray<string> Searchable,
    EquatableArray<string> Sortable,
    EquatableArray<FieldDescription> Fields,
    string? DescribedName = null)
{
    /// <summary>What a server returns (as opposed to what a caller sends).</summary>
    public bool IsRead => Role is "read" or "nested-read";

    public static TypeDescription Read(JsonElement element) => new(
        element.GetProperty("name").GetString()!,
        element.GetProperty("role").GetString()!,
        ApiDescription.Text(element, "summary"),
        ApiDescription.Text(element, "object"),
        ApiDescription.Text(element, "method"),
        ApiDescription.Flag(element, "extensionData"),
        ApiDescription.Flag(element, "getParameters"),
        ApiDescription.Strings(element, "filterable"),
        ApiDescription.Strings(element, "searchable"),
        ApiDescription.Strings(element, "sortable"),
        [.. element.GetProperty("fields").EnumerateArray().Select(FieldDescription.Read)],
        element.GetProperty("name").GetString());
}
