// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.Generator;

/// <summary>
/// The API object an id field refers to (<c>references</c> in <c>schema/zabbix-api.json</c>): <c>hostid</c> names a
/// host. A get option of the same name plus <c>s</c> selects by that object. An entry with an <see cref="Owner"/> holds
/// only under that object, in its requests and its get (a template's <c>groupid</c> names a template group).
/// </summary>
/// <param name="Owner">The object the entry holds under; <see langword="null"/> everywhere else.</param>
/// <param name="Wire">The id field's wire name, singular (<c>groupid</c>).</param>
/// <param name="Object">The API object it refers to (<c>hostgroup</c>).</param>
internal sealed record IdReference(string? Owner, string Wire, string Object)
{
    /// <summary>
    /// The description's <c>references</c> section (<c>ids</c>, then <c>owners</c>); none when it has no such section. A
    /// value that is not an object's name is a <see cref="ZabbixDiagnostics.Malformed"/> problem, and left out.
    /// </summary>
    /// <param name="root">The description.</param>
    /// <param name="problems">Receives what was left out.</param>
    public static EquatableArray<IdReference> ReadAll(JsonElement root, List<EmitProblem> problems)
    {
        if (!root.TryGetProperty("references", out JsonElement section) || section.ValueKind != JsonValueKind.Object) return [];
        var list = new List<IdReference>();
        if (section.TryGetProperty("ids", out JsonElement ids) && ids.ValueKind == JsonValueKind.Object)
            Add(null, ids);
        if (section.TryGetProperty("owners", out JsonElement owners) && owners.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty owner in owners.EnumerateObject())
                if (owner.Value.ValueKind == JsonValueKind.Object) Add(owner.Name, owner.Value);
        }
        return [.. list];

        void Add(string? owner, JsonElement map)
        {
            foreach (JsonProperty id in map.EnumerateObject())
            {
                if (id.Value.ValueKind == JsonValueKind.String) list.Add(new(owner, id.Name, id.Value.GetString()!));
                else problems.Add(new(ZabbixDiagnostics.Malformed, "the reference does not name an object and is left out", "references", id.Name));
            }
        }
    }
}
