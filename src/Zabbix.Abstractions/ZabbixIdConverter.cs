// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>A Zabbix id is a number on the server and a string in the API's JSON: this reads both and writes the string the API sends.</summary>
internal sealed class ZabbixIdConverter : JsonConverter<ulong>
{
    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number when reader.TryGetUInt64(out ulong id) => id,
        JsonTokenType.String when !reader.HasValueSequence && !reader.ValueIsEscaped && ulong.TryParse(reader.ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) => id,
        JsonTokenType.String when ulong.TryParse(reader.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) => id,
        _ => throw new JsonException($"A {reader.TokenType} is not a Zabbix id: an id is a decimal number such as 10084."),
    };

    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> digits = stackalloc byte[20];
        value.TryFormat(digits, out int written, default, CultureInfo.InvariantCulture);
        writer.WriteStringValue(digits[..written]);
    }

    public override ulong ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ulong.TryParse(reader.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : throw new JsonException("A property name is not a Zabbix id: an id is a decimal number such as 10084.");

    public override void WriteAsPropertyName(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.ToString(CultureInfo.InvariantCulture));
    }
}
