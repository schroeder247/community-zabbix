// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix;

/// <summary>Reading the scalar results the generated clients return: a count, a text; and the parameters a count sends.</summary>
internal static class ZabbixApiResults
{
    /// <summary>
    /// The parameters as a count sends them: their own fields, and <c>countOutput</c>, which a get's parameters leave out
    /// because it changes the shape of the reply (ADR 0013, section 4).
    /// </summary>
    /// <typeparam name="TParameters">The get's parameters.</typeparam>
    /// <param name="parameters">What to count.</param>
    /// <param name="info">How the parameters are written.</param>
    /// <returns>The parameters with <c>countOutput</c>.</returns>
    internal static JsonNode Counting<TParameters>(TParameters parameters, JsonTypeInfo<TParameters> info)
    {
        JsonObject counting = JsonSerializer.SerializeToNode(parameters, info) as JsonObject
            ?? throw new InvalidOperationException("A get's parameters are written as a JSON object.");
        counting["countOutput"] = true;
        return counting;
    }

    /// <summary>A <c>countOutput</c> result: the API writes it as a number or as text. Anything else is not a count and throws (it is never read as 0).</summary>
    /// <param name="result">The result.</param>
    /// <param name="method">The method, for the message.</param>
    /// <exception cref="InvalidDataException">The result is not a count.</exception>
    /// <returns>The count the server answered.</returns>
    internal static long ReadCount(JsonElement result, string method)
    {
        if (result.ValueKind == JsonValueKind.Number && result.TryGetInt64(out long number)) return number;
        if (result.ValueKind == JsonValueKind.String && long.TryParse(result.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)) return parsed;
        throw new InvalidDataException($"The {method} reply with countOutput is a {result.ValueKind}, which is not a count.");
    }

    /// <summary>A text result (<c>apiinfo.version</c>, <c>configuration.export</c>).</summary>
    /// <param name="result">The result.</param>
    /// <param name="method">The method, for the message.</param>
    /// <exception cref="InvalidDataException">The result is not text.</exception>
    /// <returns>The text the server answered.</returns>
    internal static string ReadText(JsonElement result, string method)
        => result.ValueKind == JsonValueKind.String && result.GetString() is { } text
            ? text
            : throw new InvalidDataException($"The {method} reply is a {result.ValueKind} where the API returns text.");
}
