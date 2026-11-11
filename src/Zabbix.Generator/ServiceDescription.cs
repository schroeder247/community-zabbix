// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.Generator;

/// <summary>
/// An API object and its methods. <see cref="CmdletNoun"/> is the noun its commands are named with where its client was
/// respelled (<see cref="ZabbixRespellings"/>): the modules keep their commands.
/// </summary>
internal sealed record ServiceDescription(
    string Object,
    string Client,
    string Property,
    string? Summary,
    string? Model,
    string? Since,
    string? Removed,
    EquatableArray<MethodDescription> Methods,
    ServiceHelp? Help = null,
    string? IdField = null,
    EquatableArray<WireNote> RequiredWhen = default,
    string? CmdletNoun = null)
{
    public static ServiceDescription Read(JsonElement element) => new(
        element.GetProperty("object").GetString()!,
        element.GetProperty("client").GetString()!,
        element.GetProperty("property").GetString()!,
        ApiDescription.Text(element, "summary"),
        ApiDescription.Text(element, "model"),
        ApiDescription.Text(element, "since"),
        ApiDescription.Text(element, "removed"),
        [.. element.GetProperty("methods").EnumerateArray().Select(MethodDescription.Read)],
        ServiceHelp.Read(element),
        ApiDescription.Text(element, "idField"),
        element.TryGetProperty("requiredWhen", out JsonElement conditions) && conditions.ValueKind == JsonValueKind.Object
            ? [.. conditions.EnumerateObject().Select(c => new WireNote(c.Name, c.Value.GetString() ?? throw new InvalidOperationException("a requiredWhen condition is not a string")))]
            : []);
}
