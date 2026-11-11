// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// A list of text the API returns either as an array or, for some rows, as one string: a user's media <c>sendto</c> is an
/// array for e-mail and push media and a string for every other type (<c>CUser::addRelatedObjects</c>, 8.0:3540-3553,
/// <c>explode("\n", …)</c> for those types only). One string reads as a list of that one string; <see langword="null"/>
/// reads as <see langword="null"/>. Anything else is not text and is a <see cref="JsonException"/>. Writes an array. Owned
/// because the platform reads one string into a list only as an error.
/// </summary>
internal sealed class ZabbixTextListConverter : JsonConverter<IReadOnlyList<string>>
{
    /// <inheritdoc/>
    public override bool HandleNull => false;

    /// <inheritdoc/>
    public override IReadOnlyList<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return [reader.GetString() ?? string.Empty];
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException($"A {reader.TokenType} is not text or a list of text.");
        var texts = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException($"A list of text holds a {reader.TokenType}.");
            texts.Add(reader.GetString() ?? string.Empty);
        }
        return texts.AsReadOnly();
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartArray();
        foreach (string text in value) writer.WriteStringValue(text);
        writer.WriteEndArray();
    }
}
