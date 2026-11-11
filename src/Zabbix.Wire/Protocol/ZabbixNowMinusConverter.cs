// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix.Protocol;

/// <summary>
/// How long before now a value was collected, as <c>item.test</c> reads a previous value's <c>timestamp</c>: <c>now</c>,
/// or <c>now-</c> and a length of time with an optional unit suffix (<c>now-30s</c>, <c>now-1h</c>;
/// <c>trapper_parse_preproc_test</c> in <c>trapper_preproc.c</c>). Written as whole seconds.
/// </summary>
internal sealed class ZabbixNowMinusConverter : JsonConverter<TimeSpan>
{
    private const string Now = "now";

    /// <inheritdoc/>
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (text is null || !text.StartsWith(Now, StringComparison.Ordinal))
            throw new JsonException($"A {reader.TokenType} is not a timestamp item.test reads: it is now or now-<length>, such as now-30s.");
        if (text.Length == Now.Length) return TimeSpan.Zero;
        if (text[Now.Length] != '-' || !TryLength(text.AsSpan(Now.Length + 1), out TimeSpan length))
            throw new JsonException($"'{text}' is not a timestamp item.test reads: it is now or now-<length>, such as now-30s.");
        return length;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        long seconds = (long)value.TotalSeconds;
        writer.WriteStringValue(seconds <= 0 ? Now : string.Create(CultureInfo.InvariantCulture, $"{Now}-{seconds}s"));
    }

    // zbx_is_time_suffix: digits and one of s, m, h, d, w, or no suffix for seconds.
    private static bool TryLength(ReadOnlySpan<char> text, out TimeSpan length)
    {
        length = default;
        if (text.IsEmpty) return false;
        long unit = text[^1] switch
        {
            's' => 1,
            'm' => 60,
            'h' => 3600,
            'd' => 86400,
            'w' => 604800,
            _ => 0,
        };
        ReadOnlySpan<char> digits = unit == 0 ? text : text[..^1];
        if (unit == 0) unit = 1;
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long count) || count > long.MaxValue / unit / TimeSpan.TicksPerSecond) return false;
        length = TimeSpan.FromSeconds(count * unit);
        return true;
    }
}
