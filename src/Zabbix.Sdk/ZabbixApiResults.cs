// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix;

/// <summary>
/// Reading the results the generated clients don't hand to a record's type as they are: a count, a text, a <c>true</c>,
/// a record or its absence, a record or its text; and the parameters a count sends.
/// </summary>
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

    /// <summary>A write's <c>true</c>: anything else is not the API's answer to it and throws.</summary>
    /// <param name="result">The result.</param>
    /// <param name="method">The method, for the message.</param>
    /// <exception cref="InvalidDataException">The result is not <c>true</c>.</exception>
    internal static void ReadTrue(JsonElement result, string method)
    {
        if (result.ValueKind != JsonValueKind.True)
            throw new InvalidDataException($"The {method} reply is a {result.ValueKind} where the API returns true.");
    }

    /// <summary>
    /// A record the API answers as <c>true</c> or as an empty list when it has nothing to say (PHP encodes an empty array as
    /// <c>[]</c>): either reads as an empty record, and an object as the record.
    /// </summary>
    /// <typeparam name="TResult">The record.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="info">How the record is read.</param>
    /// <param name="method">The method, for the message.</param>
    /// <exception cref="InvalidDataException">The result is none of those.</exception>
    /// <returns>The record the server answered, or an empty one.</returns>
    internal static TResult ReadOrEmpty<TResult>(JsonElement result, JsonTypeInfo<TResult> info, string method)
        where TResult : new()
        => result.ValueKind switch
        {
            JsonValueKind.True => new TResult(),
            JsonValueKind.Array when result.GetArrayLength() == 0 => new TResult(),
            JsonValueKind.Object => result.Deserialize(info) ?? new TResult(),
            _ => throw new InvalidDataException($"The {method} reply is a {result.ValueKind} where the API returns an object, true or an empty list."),
        };

    /// <summary>
    /// A record the API answers as the text of one of its fields unless the request asks for the record (<c>user.login</c>
    /// answers the session id, or the user with <c>userData</c>).
    /// </summary>
    /// <typeparam name="TResult">The record.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="info">How the record is read.</param>
    /// <param name="fromText">The record that holds the text.</param>
    /// <param name="method">The method, for the message.</param>
    /// <exception cref="InvalidDataException">The result is neither text nor an object.</exception>
    /// <returns>The record the server answered.</returns>
    internal static TResult ReadOrText<TResult>(JsonElement result, JsonTypeInfo<TResult> info, Func<string, TResult> fromText, string method)
        => result.ValueKind switch
        {
            JsonValueKind.String when result.GetString() is { } text => fromText(text),
            JsonValueKind.Object => result.Deserialize(info) ?? throw new InvalidDataException($"The {method} reply is null."),
            _ => throw new InvalidDataException($"The {method} reply is a {result.ValueKind} where the API returns text or an object."),
        };
}
