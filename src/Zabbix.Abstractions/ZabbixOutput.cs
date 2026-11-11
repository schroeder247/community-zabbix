// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents what a <c>.get</c> returns for an object: everything, a count, or these fields. The API's <c>output</c> and
/// <c>select*</c> parameters are a keyword or a list of field names, and this type is both — implicitly from a
/// string, a list or a collection expression, written to the wire as whichever it is. Equal when the keyword or the field sequence is.
/// </summary>
/// <remarks>
/// Owned: the grammar is <c>"extend" | "count" | [field, ...]</c>, a keyword-or-list union no BCL type spells, and
/// <see cref="System.Text.Json.Nodes.JsonNode"/> would admit any JSON (an object, a number) the API refuses. Equality is
/// written over the fields: a record's synthesized equality compares an <see cref="IReadOnlyList{T}"/> by reference, so
/// <c>Of("a", "b") == Of("a", "b")</c> would be false.
/// </remarks>
[JsonConverter(typeof(ZabbixOutputConverter))]
[CollectionBuilder(typeof(ZabbixOutput), nameof(Of))]
public sealed record ZabbixOutput
{
    private ZabbixOutput(string? keyword, IReadOnlyList<string>? fields)
    {
        Keyword = keyword;
        Fields = fields;
    }

    /// <summary><c>extend</c>: every field.</summary>
    public static ZabbixOutput Extend { get; } = new("extend", null);

    /// <summary><c>count</c>: how many.</summary>
    public static ZabbixOutput Count { get; } = new("count", null);

    /// <summary>The keyword, when this is one.</summary>
    public string? Keyword { get; }

    /// <summary>The fields, when this is a list.</summary>
    public IReadOnlyList<string>? Fields { get; }

    /// <summary>These fields.</summary>
    /// <param name="fields">Wire field names.</param>
    /// <returns>The output naming the fields.</returns>
    public static ZabbixOutput Of(params ReadOnlySpan<string> fields) => new(null, [.. fields]);

    /// <summary>A keyword (<c>extend</c>, <c>count</c>) or one field.</summary>
    /// <param name="keywordOrField">The word.</param>
    /// <returns>The output naming what was converted.</returns>
    public static implicit operator ZabbixOutput(string keywordOrField)
        => keywordOrField is "extend" or "count" ? new(keywordOrField, null) : new(null, [keywordOrField]);

    /// <summary>These fields.</summary>
    /// <param name="fields">Wire field names.</param>
    /// <returns>The output naming what was converted.</returns>
    public static implicit operator ZabbixOutput(string[] fields) => new(null, fields is null ? null : [.. fields]);   // a copy: the caller's array stays theirs

    /// <summary>A keyword or one field (the named form of the implicit conversion).</summary>
    /// <param name="keywordOrField">The word.</param>
    /// <returns>The output naming the keyword.</returns>
    public static ZabbixOutput FromString(string keywordOrField) => keywordOrField;

    /// <summary>These fields (the named form of the implicit conversion).</summary>
    /// <param name="fields">Wire field names.</param>
    /// <returns>The output naming the fields.</returns>
    public static ZabbixOutput FromStringArray(string[] fields) => fields;

    /// <summary>
    /// Enumerates the fields this output names, none for a keyword: the iteration a collection expression
    /// (<c>Output = ["hostid", "name"]</c>) needs. The type is not an <see cref="IEnumerable{T}"/>, so a serializer or a
    /// shell treats it as the one value it is.
    /// </summary>
    /// <returns>An enumerator over <see cref="Fields"/>.</returns>
    public IEnumerator<string> GetEnumerator() => (Fields ?? []).GetEnumerator();

    /// <summary>The same keyword, or the same fields in the same order.</summary>
    /// <param name="other">The other output.</param>
    /// <returns><see langword="true"/> when both name the same keyword or the same fields in the same order; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixOutput? other)
        => other is not null && string.Equals(Keyword, other.Keyword, StringComparison.Ordinal)
            && (Fields ?? []).SequenceEqual(other.Fields ?? [], StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Keyword, StringComparer.Ordinal);
        foreach (string field in Fields ?? []) hash.Add(field, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => Keyword ?? string.Join(",", Fields ?? []);
}

/// <summary>Writes a <see cref="ZabbixOutput"/> as its keyword or its list, and reads either; owned because the value is a union the platform has no converter for.</summary>
internal sealed class ZabbixOutputConverter : JsonConverter<ZabbixOutput>
{
    /// <inheritdoc/>
    public override ZabbixOutput? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString() ?? string.Empty;
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("An output is a keyword or a list of fields.");
        var fields = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            fields.Add(reader.TokenType == JsonTokenType.String ? reader.GetString() ?? string.Empty : throw new JsonException($"An output's list of fields holds a {reader.TokenType}."));
        return fields.ToArray();
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ZabbixOutput value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Keyword is { } keyword) { writer.WriteStringValue(keyword); return; }
        writer.WriteStartArray();
        foreach (string field in value.Fields ?? []) writer.WriteStringValue(field);
        writer.WriteEndArray();
    }
}
