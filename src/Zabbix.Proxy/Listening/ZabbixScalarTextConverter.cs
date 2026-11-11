// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix.Listening;

/// <summary>
/// A JSON scalar read as its text, as upstream's <c>zbx_json_value_by_name_dyn</c> reads a sender data row
/// (<c>proxy.c</c> <c>parse_history_data_row_value</c>; <c>zbx_json_decodevalue_dyn</c> in <c>json.c</c>): a string
/// decoded, a number or <c>true</c>/<c>false</c> as written, <c>null</c> as nothing, and an array or object as absent (upstream
/// decodes only primitives), skipped rather than failing the whole frame. So <c>{"value":42.5}</c>, a script's
/// <c>json.dumps</c> of a number, is the value <c>"42.5"</c>.
/// </summary>
/// <remarks>
/// Owned: System.Text.Json reads a number into a <see cref="string"/> under no option (<see cref="JsonNumberHandling"/>
/// only reads strings into numbers), and a <see cref="JsonElement"/> member would move the kind check to every reader of
/// the record. Writes a string, as a sender sends one.
/// </remarks>
internal sealed class ZabbixScalarTextConverter : JsonConverter<string?>
{
    public override bool HandleNull => true;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False:
                return reader.HasValueSequence ? Encoding.UTF8.GetString(reader.ValueSequence) : Encoding.UTF8.GetString(reader.ValueSpan);
            case JsonTokenType.StartArray or JsonTokenType.StartObject:
                reader.Skip();
                return null;
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
