// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix;

/// <summary>Reading the ids a write returns: <c>{"hostids": ["1", "2"]}</c>.</summary>
internal static class ZabbixIds
{
    /// <summary><c>{}</c>: what a method that takes nothing is sent.</summary>
    public static JsonElement EmptyObject { get; } = JsonElement.Parse("{}");

    /// <summary>The ids under <paramref name="field"/>.</summary>
    /// <param name="result">The API's result.</param>
    /// <param name="field">The array's name, e.g. <c>hostids</c>.</param>
    /// <exception cref="InvalidDataException">The result carries no such array, or an element of it is not an id (a decimal string or an unsigned integer).</exception>
    public static IReadOnlyList<ulong> Read(JsonElement result, string field)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(field, out JsonElement ids) || ids.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"The result carries no '{field}' array.");
        var list = new List<ulong>(ids.GetArrayLength());
        foreach (JsonElement id in ids.EnumerateArray())
        {
            list.Add(id.ValueKind switch
            {
                JsonValueKind.String when ulong.TryParse(id.GetString(), null, out ulong parsed) => parsed,
                JsonValueKind.Number when id.TryGetUInt64(out ulong number) => number,
                _ => throw new InvalidDataException($"The result's '{field}' holds a {id.ValueKind} that is not an id (a decimal string or an unsigned integer)."),
            });
        }
        return list;
    }

    /// <summary>The one id under <paramref name="field"/>: the answer to a write of one object.</summary>
    /// <param name="result">The API's result.</param>
    /// <param name="field">The array's name, e.g. <c>hostids</c>.</param>
    /// <exception cref="InvalidDataException">The result carries no such array, or not exactly one id in it.</exception>
    public static ulong ReadOne(JsonElement result, string field)
        => Read(result, field) is [var one]
            ? one
            : throw new InvalidDataException($"The result's '{field}' does not hold exactly the one id a write of one object answers.");
}
