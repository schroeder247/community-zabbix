// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// A length of time Zabbix writes as whole seconds, a JSON number or its decimal text (an SLA's uptime, a server's
/// uptime). A fraction or anything else is a <see cref="JsonException"/>.
/// </summary>
internal sealed class ZabbixSecondsConverter : JsonConverter<TimeSpan>
{
    /// <inheritdoc/>
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ZabbixWholeUnits.Read(ref reader, TimeSpan.TicksPerSecond, "seconds");

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(value.Ticks / TimeSpan.TicksPerSecond);
    }
}

/// <summary>
/// A length of time Zabbix writes as whole milliseconds, a JSON number or its decimal text (how long a script ran). A
/// fraction or anything else is a <see cref="JsonException"/>.
/// </summary>
internal sealed class ZabbixMillisecondsConverter : JsonConverter<TimeSpan>
{
    /// <inheritdoc/>
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ZabbixWholeUnits.Read(ref reader, TimeSpan.TicksPerMillisecond, "milliseconds");

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(value.Ticks / TimeSpan.TicksPerMillisecond);
    }
}

/// <summary>Reads a whole number of a unit of time as a <see cref="TimeSpan"/>.</summary>
internal static class ZabbixWholeUnits
{
    /// <summary>The number the reader is on, as a length of time.</summary>
    /// <param name="reader">The reader, on a number or a string.</param>
    /// <param name="ticksPerUnit">How many ticks one unit is.</param>
    /// <param name="unit">The unit's name, for the message.</param>
    /// <returns>The length of time.</returns>
    /// <exception cref="JsonException">The token is not a whole number, or the length doesn't fit a <see cref="TimeSpan"/>.</exception>
    public static TimeSpan Read(ref Utf8JsonReader reader, long ticksPerUnit, string unit)
    {
        long count = reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt64(out long number) => number,
            JsonTokenType.String when long.TryParse(reader.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number) => number,
            _ => throw new JsonException($"A {reader.TokenType} is not a length of time: Zabbix writes it as whole {unit}."),
        };
        if (count > TimeSpan.MaxValue.Ticks / ticksPerUnit || count < TimeSpan.MinValue.Ticks / ticksPerUnit)
            throw new JsonException($"{count} {unit} is outside the range of a length of time.");
        return TimeSpan.FromTicks(count * ticksPerUnit);
    }
}
