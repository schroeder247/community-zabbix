// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents an update interval as Zabbix spells it: how often an item, an item prototype or a discovery rule is
/// checked. A <see cref="TimeSpan"/> converts to one, <c>i.Delay = TimeSpan.FromMinutes(5)</c> sends <c>5m</c>, and
/// <see cref="Parse(string)"/> takes the rest of the grammar: <c>{$DELAY}</c>, or <c>1m;10s/1-5,09:00-18:00</c>, a
/// flexible interval that checks every 10 seconds in office hours.
/// </summary>
/// <remarks>
/// <para>
/// An update interval is a whole number of seconds, written bare or with an <c>s</c>, <c>m</c>, <c>h</c>, <c>d</c> or
/// <c>w</c> suffix, or a macro, then any number of flexible or scheduling intervals, each after a <c>;</c>. A flexible
/// interval is a length, a <c>/</c> and a period (<c>1-5,09:00-18:00</c>, or a macro); a scheduling interval is one of the
/// <c>md</c>, <c>wd</c>, <c>h</c>, <c>m</c> and <c>s</c> filters Zabbix schedules by (<c>wd1-5h9</c>), or a macro. A macro
/// is a user macro, or an LLD macro, which Zabbix takes in a prototype's interval, and is kept as it's written.
/// </para>
/// <para>
/// Parsing checks the grammar, and leaves the ranges to the server, which knows what the item is: a trapper item's
/// interval is <c>0</c>, and a polled item's is at most a day. Intervals are equal when they're written the same, as
/// Zabbix stores what it's given. The default value is <c>0</c>. A value read from a reply, the API's, an agent's list of
/// checks or a proxy's configuration, is kept as the server wrote it, even where a newer Zabbix writes more than this
/// grammar; <see cref="UpdateInterval"/> then reads the length the text starts with, if any.
/// </para>
/// </remarks>
[JsonConverter(typeof(ZabbixIntervalConverter))]
public readonly record struct ZabbixInterval : ISpanParsable<ZabbixInterval>
{
    // The text, as written; null for "0", so the default value is the zero interval.
    private readonly string? _text;

    private ZabbixInterval(string text) => _text = text == "0" ? null : text;

    /// <summary>
    /// Gets the update interval: the length before any flexible or scheduling interval; <see langword="null"/> when it's a
    /// macro, or a reply's text that doesn't start with a length.
    /// </summary>
    public TimeSpan? UpdateInterval
    {
        get
        {
            if (_text is null) return TimeSpan.Zero;
            long? seconds = ZabbixIntervalGrammar.Length(_text, out int length);
            return seconds is { } whole && (length == _text.Length || _text[length] == ';') ? TimeSpan.FromSeconds(whole) : null;
        }
    }

    /// <summary>Converts a <see cref="TimeSpan"/> to the interval that's that long, written with the largest suffix that divides it: 30 seconds is <c>30s</c>, an hour <c>1h</c>.</summary>
    /// <param name="interval">The interval: whole seconds, and not negative.</param>
    /// <returns>The interval.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> is negative, isn't a whole number of seconds, or is longer than 2,147,483,647 seconds.</exception>
    public static implicit operator ZabbixInterval(TimeSpan interval) => FromTimeSpan(interval);

    /// <summary>Creates the interval that's <paramref name="interval"/> long: the named form of the implicit conversion.</summary>
    /// <param name="interval">The interval: whole seconds, and not negative.</param>
    /// <returns>The interval.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> is negative, isn't a whole number of seconds, or is longer than 2,147,483,647 seconds.</exception>
    public static ZabbixInterval FromTimeSpan(TimeSpan interval) => new(ZabbixDuration.Text(interval, nameof(interval)));

    /// <summary>Parses Zabbix's spelling of an update interval: <c>30s</c>, <c>{$DELAY}</c>, <c>1m;10s/1-5,09:00-18:00</c>.</summary>
    /// <param name="s">The interval, as Zabbix spells it.</param>
    /// <returns>The interval.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="s"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="s"/> isn't an interval in Zabbix's grammar.</exception>
    public static ZabbixInterval Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan());
    }

    /// <summary>Parses Zabbix's spelling of an update interval: <c>30s</c>, <c>{$DELAY}</c>, <c>1m;10s/1-5,09:00-18:00</c>.</summary>
    /// <param name="s">The interval, as Zabbix spells it.</param>
    /// <returns>The interval.</returns>
    /// <exception cref="FormatException"><paramref name="s"/> isn't an interval in Zabbix's grammar.</exception>
    public static ZabbixInterval Parse(ReadOnlySpan<char> s)
        => TryParse(s, out ZabbixInterval result) ? result
            : throw new FormatException($"'{s}' isn't an interval Zabbix takes: write a whole number of seconds with an optional s, m, h, d or w suffix, or a macro, then any flexible (1m/1-5,09:00-18:00) or scheduling (wd1-5h9) intervals, each after a ';'.");

    /// <summary>Parses Zabbix's spelling of an update interval, and says whether it is one.</summary>
    /// <param name="s">The interval, as Zabbix spells it.</param>
    /// <param name="result">The interval, when <paramref name="s"/> is one.</param>
    /// <returns><see langword="true"/> when <paramref name="s"/> is an interval in Zabbix's grammar; otherwise <see langword="false"/>.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, out ZabbixInterval result)
    {
        result = default;
        return s is not null && TryParse(s.AsSpan(), out result);
    }

    /// <summary>Parses Zabbix's spelling of an update interval, and says whether it is one.</summary>
    /// <param name="s">The interval, as Zabbix spells it.</param>
    /// <param name="result">The interval, when <paramref name="s"/> is one.</param>
    /// <returns><see langword="true"/> when <paramref name="s"/> is an interval in Zabbix's grammar; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, out ZabbixInterval result)
    {
        result = default;
        if (!ZabbixIntervalGrammar.TryParse(s, out _, out _, out _)) return false;
        result = new(s.ToString());
        return true;
    }

    /// <inheritdoc/>
    static ZabbixInterval IParsable<ZabbixInterval>.Parse(string s, IFormatProvider? provider) => Parse(s);

    /// <inheritdoc/>
    static bool IParsable<ZabbixInterval>.TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ZabbixInterval result) => TryParse(s, out result);

    /// <inheritdoc/>
    static ZabbixInterval ISpanParsable<ZabbixInterval>.Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s);

    /// <inheritdoc/>
    static bool ISpanParsable<ZabbixInterval>.TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out ZabbixInterval result) => TryParse(s, out result);

    /// <summary>Returns the interval as Zabbix spells it.</summary>
    /// <returns>The interval's text: <c>0</c> for the default value.</returns>
    public override string ToString() => _text ?? "0";

    /// <summary>The interval a reply holds, kept as the server wrote it whether or not it fits the grammar.</summary>
    /// <param name="text">The server's text.</param>
    /// <returns>The interval.</returns>
    internal static ZabbixInterval FromReply(string text) => new(text);
}

/// <summary>Writes a <see cref="ZabbixInterval"/> as Zabbix spells it, and reads the text, or the number of seconds, a reply holds.</summary>
internal sealed class ZabbixIntervalConverter : JsonConverter<ZabbixInterval>
{
    /// <inheritdoc/>
    public override ZabbixInterval Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => ZabbixInterval.FromReply(reader.GetString() ?? string.Empty),
        JsonTokenType.Number when reader.TryGetInt64(out long seconds) => ZabbixInterval.FromReply(seconds.ToString(CultureInfo.InvariantCulture)),
        _ => throw new JsonException($"An interval is text, not a {reader.TokenType}."),
    };

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ZabbixInterval value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
