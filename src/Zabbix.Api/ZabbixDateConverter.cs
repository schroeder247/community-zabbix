// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// A date the API reads and writes as <c>YYYY-MM-DD</c> (<c>API_DATE</c>, a report's <c>active_since</c>): a
/// <see cref="DateOnly"/>. An empty text is no date (the API's own "not set"); any other text that is not a date is a
/// <see cref="JsonException"/>. Owned because the platform's <see cref="DateOnly"/> converter reads ISO 8601 only and
/// refuses the empty text.
/// </summary>
internal sealed class ZabbixDateConverter : JsonConverter<DateOnly?>
{
    private const string Format = "yyyy-MM-dd";

    /// <inheritdoc/>
    public override bool HandleNull => true;

    /// <inheritdoc/>
    public override DateOnly? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Null => null,
        JsonTokenType.String when reader.GetString() is "" => null,
        JsonTokenType.String when DateOnly.TryParseExact(reader.GetString(), Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date) => date,
        _ => throw new JsonException($"A {reader.TokenType} is not a date: a date is written YYYY-MM-DD."),
    };

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DateOnly? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is { } date) writer.WriteStringValue(date.ToString(Format, CultureInfo.InvariantCulture));
        else writer.WriteNullValue();
    }
}
