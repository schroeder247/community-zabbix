// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents what a get returns of <typeparamref name="TRecord"/>: every field, a count, or the fields a list names. A
/// get's <c>output</c> and its <c>select</c> options each take one, typed by the record they return, so
/// <c>q.Output = [ZabbixHostFields.HostId]</c> compiles in a host's query and not in an item's. A keyword converts from
/// <see cref="ZabbixOutput.Extend"/> or <see cref="ZabbixOutput.Count"/>, and a list is a collection expression of
/// <see cref="ZabbixField{TRecord}"/> values. Equal when the keyword is, or the fields in order are.
/// </summary>
/// <remarks>
/// Owned: the grammar is <c>"extend" | "count" | [field, ...]</c>, a keyword-or-list union no BCL type spells. Equality is
/// written over the fields, since a record's synthesized equality compares an <see cref="IReadOnlyList{T}"/> by
/// reference.
/// </remarks>
/// <typeparam name="TRecord">The record whose fields the output names.</typeparam>
[CollectionBuilder(typeof(ZabbixOutput), nameof(ZabbixOutput.Of))]
public sealed record ZabbixOutput<TRecord>
{
    private ZabbixOutput(string? keyword, IReadOnlyList<ZabbixField<TRecord>>? fields)
    {
        Keyword = keyword;
        Fields = fields;
    }

    /// <summary>Gets the keyword, <c>extend</c> or <c>count</c>, when the output is one.</summary>
    public string? Keyword { get; }

    /// <summary>Gets the fields, in the order they're sent, when the output is a list.</summary>
    public IReadOnlyList<ZabbixField<TRecord>>? Fields { get; }

    /// <summary>Converts a keyword to the output of <typeparamref name="TRecord"/> that sends it.</summary>
    /// <param name="keyword">The keyword: <see cref="ZabbixOutput.Extend"/> or <see cref="ZabbixOutput.Count"/>.</param>
    /// <returns>The output, or <see langword="null"/> when <paramref name="keyword"/> is.</returns>
    [return: NotNullIfNotNull(nameof(keyword))]
    public static implicit operator ZabbixOutput<TRecord>?(ZabbixOutput? keyword) => FromZabbixOutput(keyword);

    /// <summary>Creates the output of <typeparamref name="TRecord"/> that sends a keyword: the named form of the implicit conversion.</summary>
    /// <param name="keyword">The keyword: <see cref="ZabbixOutput.Extend"/> or <see cref="ZabbixOutput.Count"/>.</param>
    /// <returns>The output, or <see langword="null"/> when <paramref name="keyword"/> is.</returns>
    [return: NotNullIfNotNull(nameof(keyword))]
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "The named form of the implicit conversion, as ZabbixOptional<T>.FromNullable is; the type argument is inferred from the target.")]
    public static ZabbixOutput<TRecord>? FromZabbixOutput(ZabbixOutput? keyword) => keyword is null ? null : new(keyword.Keyword, null);

    /// <summary>
    /// Enumerates the fields this output names, none for a keyword: the iteration a collection expression needs. The type
    /// isn't an <see cref="IEnumerable{T}"/>, so a serializer or a shell treats it as the one value it is.
    /// </summary>
    /// <returns>An enumerator over <see cref="Fields"/>.</returns>
    public IEnumerator<ZabbixField<TRecord>> GetEnumerator() => (Fields ?? []).GetEnumerator();

    /// <summary>Whether <paramref name="other"/> sends the same keyword, or the same fields in the same order.</summary>
    /// <param name="other">The output to compare with this one; <see langword="null"/> is never equal.</param>
    /// <returns><see langword="true"/> when both send the same keyword or the same fields in order; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixOutput<TRecord>? other)
        => other is not null && string.Equals(Keyword, other.Keyword, StringComparison.Ordinal)
            && (Fields ?? []).SequenceEqual(other.Fields ?? []);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Keyword, StringComparer.Ordinal);
        foreach (ZabbixField<TRecord> field in Fields ?? []) hash.Add(field);
        return hash.ToHashCode();
    }

    /// <summary>Returns the keyword, or the fields' names joined by commas.</summary>
    /// <returns>The output as text.</returns>
    public override string ToString() => Keyword ?? string.Join(",", (Fields ?? []).Select(field => field.Name));

    /// <summary>The output that names <paramref name="fields"/>, each of which must name a field.</summary>
    /// <param name="fields">The fields, in order.</param>
    /// <returns>The output.</returns>
    /// <exception cref="ArgumentException">A field is the default value.</exception>
    internal static ZabbixOutput<TRecord> FromFields(ReadOnlySpan<ZabbixField<TRecord>> fields)
    {
        foreach (ZabbixField<TRecord> field in fields) _ = ZabbixFieldListConverter<TRecord>.Named(field);
        return new(null, [.. fields]);
    }

    /// <summary>The output a reply or a caller's JSON spells: a keyword, or a list of names.</summary>
    /// <param name="keyword">The keyword, when the output is one.</param>
    /// <param name="fields">The fields, when the output is a list.</param>
    /// <returns>The output.</returns>
    internal static ZabbixOutput<TRecord> FromJson(string? keyword, IReadOnlyList<ZabbixField<TRecord>>? fields) => new(keyword, fields);
}

/// <summary>
/// Writes a <see cref="ZabbixOutput{TRecord}"/> as its keyword or its list of names, and reads either, or one name as a
/// list of it. Owned because the value is a union no platform converter writes, and generic, so each generated member
/// names this converter for its record.
/// </summary>
/// <typeparam name="TRecord">The record whose fields the output names.</typeparam>
internal sealed class ZabbixOutputConverter<TRecord> : JsonConverter<ZabbixOutput<TRecord>>
{
    /// <inheritdoc/>
    public override ZabbixOutput<TRecord>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string word = reader.GetString() ?? string.Empty;
            return word is "extend" or "count" ? ZabbixOutput<TRecord>.FromJson(word, null) : ZabbixOutput<TRecord>.FromJson(null, [Field(word)]);
        }
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("An output is a keyword or a list of fields.");
        var fields = new List<ZabbixField<TRecord>>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            fields.Add(reader.TokenType == JsonTokenType.String ? Field(reader.GetString()) : throw new JsonException($"An output's list of fields holds a {reader.TokenType}."));
        return ZabbixOutput<TRecord>.FromJson(null, fields);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ZabbixOutput<TRecord> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Keyword is { } keyword)
        {
            writer.WriteStringValue(keyword);
            return;
        }
        writer.WriteStartArray();
        foreach (ZabbixField<TRecord> field in value.Fields ?? []) writer.WriteStringValue(ZabbixFieldListConverter<TRecord>.Named(field));
        writer.WriteEndArray();
    }

    private static ZabbixField<TRecord> Field(string? name)
        => string.IsNullOrEmpty(name) ? throw new JsonException("An output's field is empty.") : new(name);
}
