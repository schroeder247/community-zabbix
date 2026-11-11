// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// A time the API writes as whole Unix seconds, a JSON number or its decimal text (PHP answers most as text). A fraction
/// or anything else is not a Unix time and is a <see cref="JsonException"/>. A time written with a fraction of a second
/// drops it, as the field has none.
/// </summary>
internal sealed class ZabbixUnixTimeConverter : JsonConverter<DateTimeOffset>
{
    /// <inheritdoc/>
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number when reader.TryGetInt64(out long seconds) => FromSeconds(seconds),
        JsonTokenType.String when long.TryParse(reader.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long seconds) => FromSeconds(seconds),
        _ => throw new JsonException($"A {reader.TokenType} is not a Unix time: a time is whole seconds since the epoch."),
    };

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(value.ToUnixTimeSeconds());
    }

    private static DateTimeOffset FromSeconds(long seconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException failure)
        {
            throw new JsonException($"{seconds} is outside the range of a date.", failure);
        }
    }
}
