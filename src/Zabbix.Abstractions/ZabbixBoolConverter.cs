// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// A boolean as the API speaks it on the way in — <c>true</c>, <c>1</c>, <c>"1"</c>, <c>"true"</c>, and <c>""</c> for
/// nothing — and as JSON <c>true</c>/<c>false</c> on the way out (the <c>.get</c> flags are API booleans). Owned because
/// the platform's boolean reads only JSON <c>true</c> and <c>false</c>.
/// </summary>
internal sealed class ZabbixBoolConverter : JsonConverter<bool?>
{
    /// <inheritdoc/>
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ReadBool(ref reader);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null) writer.WriteNullValue();
        else writer.WriteBooleanValue(value.Value);
    }

    internal static bool? ReadBool(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.Null => null,
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        JsonTokenType.Number => reader.GetInt64() != 0,
        JsonTokenType.String => reader.GetString() switch
        {
            "" => null,
            "1" or "true" or "True" or "TRUE" => true,
            "0" or "false" or "False" or "FALSE" => false,
            var text => throw new JsonException($"'{text}' is not a boolean."),
        },
        _ => throw new JsonException($"A {reader.TokenType} is not a boolean."),
    };
}
