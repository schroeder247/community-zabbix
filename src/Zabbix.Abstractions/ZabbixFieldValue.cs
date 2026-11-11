// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents a value as the API compares it in <c>filter</c> and <c>search</c>: a scalar or a list of scalars.
/// Implicit from the C# forms so a filter reads <c>Filter = new() { ["key_"] = key }</c>; carries whatever the API wrote when read.
/// Equal when the JSON is (<see cref="JsonElement.DeepEquals"/>).
/// </summary>
/// <remarks>
/// A typed view over <see cref="JsonElement"/>, which is the model: <see cref="Element"/> is the platform
/// type in and out. Not <see cref="JsonElement"/> bare: it has no conversion from <c>"a"</c> or <c>new[] { "a", "b" }</c>,
/// so every hand-written filter would call <c>SerializeToElement</c>. Not <see cref="System.Text.Json.Nodes.JsonNode"/>:
/// it is mutable, converts from neither array, admits objects and booleans the API refuses (<c>dbFilter</c> keeps an
/// integer column's value only when it is an int or an integer string; 8.0 rejects <c>true</c> and 7.0 selects
/// nothing), and compares by reference too. So the conversions offered are exactly the API's: text, integers, and lists
/// of either; a flag is <c>"0"</c>/<c>"1"</c>, never a boolean. Equality is written over the JSON because a record's
/// synthesized equality compares a <see cref="JsonElement"/> by its document.
/// </remarks>
[JsonConverter(typeof(ZabbixFieldValueConverter))]
public readonly record struct ZabbixFieldValue
{
    /// <summary>Wraps a JSON value.</summary>
    /// <param name="element">The JSON.</param>
    public ZabbixFieldValue(JsonElement element) => Element = element;

    /// <summary>The JSON as it goes on the wire.</summary>
    public JsonElement Element { get; }

    /// <summary>Text.</summary>
    /// <param name="value">The text.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> holds a lone surrogate, which UTF-8 JSON cannot carry.</exception>
    /// <returns>The value holding what was converted.</returns>
    public static implicit operator ZabbixFieldValue(string value) => Of(writer => writer.WriteStringValue(Sendable(value, nameof(value))));

    /// <summary>A number.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The value holding what was converted.</returns>
    public static implicit operator ZabbixFieldValue(long value) => Of(writer => writer.WriteNumberValue(value));

    /// <summary>Several texts: the API matches any of them.</summary>
    /// <param name="values">The texts.</param>
    /// <exception cref="ArgumentException">A text holds a lone surrogate, which UTF-8 JSON cannot carry.</exception>
    /// <returns>The value holding what was converted.</returns>
    public static implicit operator ZabbixFieldValue(string[] values) => Of(writer =>
    {
        writer.WriteStartArray();
        foreach (string value in values ?? []) writer.WriteStringValue(Sendable(value, nameof(values)));
        writer.WriteEndArray();
    });

    /// <summary>Several numbers: the API matches any of them.</summary>
    /// <param name="values">The numbers.</param>
    /// <returns>The value holding what was converted.</returns>
    public static implicit operator ZabbixFieldValue(long[] values) => Of(writer =>
    {
        writer.WriteStartArray();
        foreach (long value in values ?? []) writer.WriteNumberValue(value);
        writer.WriteEndArray();
    });

    /// <summary>An id, as the API writes one: its decimal text.</summary>
    /// <param name="value">The id the field is compared with.</param>
    /// <returns>The value holding what was converted.</returns>
    public static ZabbixFieldValue FromUInt64(ulong value) => value;

    /// <summary>Several ids: the API matches any of them.</summary>
    /// <param name="values">The ids, any of which the field may hold.</param>
    /// <returns>The value holding what was converted.</returns>
    public static ZabbixFieldValue FromUInt64Array(ulong[] values) => values;

    /// <summary>An id, as the API writes one: its decimal text.</summary>
    /// <param name="value">The id the field is compared with.</param>
    /// <returns>The value holding what was converted.</returns>
    public static implicit operator ZabbixFieldValue(ulong value) => Of(writer => writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Several ids: the API matches any of them.</summary>
    /// <param name="values">The ids, any of which the field may hold.</param>
    /// <returns>The value holding what was converted.</returns>
    public static implicit operator ZabbixFieldValue(ulong[] values) => Of(writer =>
    {
        writer.WriteStartArray();
        foreach (ulong value in values ?? []) writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteEndArray();
    });

    /// <summary>Text (the named form of the implicit conversion).</summary>
    /// <param name="value">The text.</param>
    /// <returns>The value holding the text.</returns>
    public static ZabbixFieldValue FromString(string value) => value;

    /// <summary>A number (the named form of the implicit conversion).</summary>
    /// <param name="value">The number.</param>
    /// <returns>The value holding the number.</returns>
    public static ZabbixFieldValue FromInt64(long value) => value;

    /// <summary>Several texts (the named form of the implicit conversion).</summary>
    /// <param name="values">The texts.</param>
    /// <returns>The value holding the texts.</returns>
    public static ZabbixFieldValue FromStringArray(string[] values) => values;

    /// <summary>Several numbers (the named form of the implicit conversion).</summary>
    /// <param name="values">The numbers.</param>
    /// <returns>The value holding the numbers.</returns>
    public static ZabbixFieldValue FromInt64Array(long[] values) => values;

    /// <summary>Whether both carry the same JSON: <see cref="JsonElement.DeepEquals"/>, so <c>1</c> equals <c>1.0</c>.</summary>
    /// <param name="other">The other value.</param>
    /// <returns><see langword="true"/> when both hold the same value of the same shape; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixFieldValue other)
        => Element.ValueKind == JsonValueKind.Undefined || other.Element.ValueKind == JsonValueKind.Undefined
            ? Element.ValueKind == other.Element.ValueKind
            : JsonElement.DeepEquals(Element, other.Element);

    /// <inheritdoc/>
    public override int GetHashCode() => Element.ValueKind switch
    {
        // Consistent with DeepEquals: a string by its text, an array by its length; numbers by kind alone, since
        // DeepEquals equates 1 and 1.0.
        JsonValueKind.String => HashCode.Combine(JsonValueKind.String, Element.GetString()),
        JsonValueKind.Array => HashCode.Combine(JsonValueKind.Array, Element.GetArrayLength()),
        var kind => kind.GetHashCode(),
    };

    /// <inheritdoc/>
    public override string ToString() => Element.ValueKind == JsonValueKind.Undefined ? string.Empty : Element.ToString();

    /// <summary>One value as itself, several as a list: how the query writes a field's values.</summary>
    internal static ZabbixFieldValue FromElements(IReadOnlyList<JsonElement> values) => values is [var one]
        ? new(one)
        : Of(writer =>
        {
            writer.WriteStartArray();
            foreach (JsonElement value in values) value.WriteTo(writer);
            writer.WriteEndArray();
        });

    private static string Sendable(string value, string paramName)
    {
        ZabbixText.ThrowIfLoneSurrogate(value, paramName);
        return value;
    }

    private static ZabbixFieldValue Of(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        return new(JsonElement.ParseValue(ref reader));
    }
}

/// <summary>Reads any JSON value into a <see cref="ZabbixFieldValue"/> and writes it back verbatim; owned because the value is a union the platform has no converter for.</summary>
internal sealed class ZabbixFieldValueConverter : JsonConverter<ZabbixFieldValue>
{
    /// <inheritdoc/>
    public override ZabbixFieldValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(JsonElement.ParseValue(ref reader));

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ZabbixFieldValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value.Element.ValueKind == JsonValueKind.Undefined) writer.WriteNullValue();
        else value.Element.WriteTo(writer);
    }
}
