// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// A time of day the API reads and writes as whole seconds after midnight, a JSON number or its decimal text (a
/// maintenance period's and a report's <c>start_time</c>): a <see cref="TimeOnly"/>. A value outside one day, a fraction
/// or anything else is a <see cref="JsonException"/>; a time written with a fraction of a second drops it, as the field
/// has none. Owned because the platform's <see cref="TimeOnly"/> converter reads ISO 8601 text only.
/// </summary>
internal sealed class ZabbixTimeOfDayConverter : JsonConverter<TimeOnly>
{
    /// <inheritdoc/>
    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number when reader.TryGetInt64(out long seconds) && InDay(seconds) => FromSeconds(seconds),
        JsonTokenType.String when long.TryParse(reader.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) && InDay(seconds) => FromSeconds(seconds),
        _ => throw new JsonException($"A {reader.TokenType} is not a time of day: a time of day is whole seconds after midnight, 0 to 86399."),
    };

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(value.Ticks / TimeSpan.TicksPerSecond);
    }

    private static bool InDay(long seconds) => seconds is >= 0 and < 86_400;

    private static TimeOnly FromSeconds(long seconds) => new(seconds * TimeSpan.TicksPerSecond);
}
