// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents a length of time as Zabbix spells it: how long an item's history and trends are kept, how long a check or a
/// script may take, how long an escalation step or a session lasts. A <see cref="TimeSpan"/> converts to one,
/// <c>i.History = TimeSpan.FromDays(90)</c> sends <c>90d</c>, and <see cref="Parse(string)"/> takes a macro:
/// <c>ZabbixDuration.Parse("{$HISTORY}")</c>.
/// </summary>
/// <remarks>
/// <para>
/// A length is a whole number of seconds, written bare or with an <c>s</c>, <c>m</c>, <c>h</c>, <c>d</c> or <c>w</c>
/// suffix, or a macro: a user macro, or an LLD macro, which Zabbix takes in a prototype. A few fields also take a month,
/// <c>M</c> (30 days), and a year, <c>y</c> (365 days): the settings' <c>max_period</c> and <c>period_default</c>, the
/// LDAP provisioning interval. It's the first part of a <see cref="ZabbixInterval"/>, without the flexible and scheduling
/// intervals that may follow one. Empty text is Zabbix's own empty value, which an item's timeout reads as its proxy's or
/// server's.
/// </para>
/// <para>
/// Parsing checks the grammar, and leaves the ranges and the suffixes a field takes to the server, which knows the field:
/// history is <c>0</c> or from an hour to 25 years, and a timeout from a second to 10 minutes. Lengths are equal when
/// they're written the same, as Zabbix stores what it's given. The default value is <c>0</c>. A value read from a reply is
/// kept as the server wrote it, even where a newer Zabbix writes more than this grammar; <see cref="Length"/> is then
/// <see langword="null"/>.
/// </para>
/// </remarks>
[JsonConverter(typeof(ZabbixDurationConverter))]
public readonly record struct ZabbixDuration : ISpanParsable<ZabbixDuration>
{
    // The text, as written; null for "0", so the default value is the zero length.
    private readonly string? _text;

    private ZabbixDuration(string text) => _text = text == "0" ? null : text;

    /// <summary>Gets the length of time; <see langword="null"/> when it's a macro, empty, or a reply's text that isn't a length.</summary>
    public TimeSpan? Length
    {
        get
        {
            if (_text is null) return TimeSpan.Zero;
            long? seconds = ZabbixIntervalGrammar.LengthWithYear(_text, out int length);
            return seconds is { } whole && length == _text.Length ? TimeSpan.FromSeconds(whole) : null;
        }
    }

    /// <summary>Converts a <see cref="TimeSpan"/> to the length it is, written with the largest suffix that divides it: 90 days is <c>90d</c>, 3 seconds <c>3s</c>.</summary>
    /// <param name="length">The length: whole seconds, and not negative.</param>
    /// <returns>The length.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative, isn't a whole number of seconds, or is longer than 2,147,483,647 seconds.</exception>
    public static implicit operator ZabbixDuration(TimeSpan length) => FromTimeSpan(length);

    /// <summary>Creates the length <paramref name="length"/> is: the named form of the implicit conversion.</summary>
    /// <param name="length">The length: whole seconds, and not negative.</param>
    /// <returns>The length.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative, isn't a whole number of seconds, or is longer than 2,147,483,647 seconds.</exception>
    public static ZabbixDuration FromTimeSpan(TimeSpan length) => new(Text(length, nameof(length)));

    /// <summary>Parses Zabbix's spelling of a length of time: <c>90d</c>, <c>3s</c>, <c>2y</c>, <c>{$HISTORY}</c>, or empty.</summary>
    /// <param name="s">The length, as Zabbix spells it.</param>
    /// <returns>The length.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="s"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="s"/> is neither a length nor a macro.</exception>
    public static ZabbixDuration Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan());
    }

    /// <summary>Parses Zabbix's spelling of a length of time: <c>90d</c>, <c>3s</c>, <c>2y</c>, <c>{$HISTORY}</c>, or empty.</summary>
    /// <param name="s">The length, as Zabbix spells it.</param>
    /// <returns>The length.</returns>
    /// <exception cref="FormatException"><paramref name="s"/> is neither a length nor a macro.</exception>
    public static ZabbixDuration Parse(ReadOnlySpan<char> s)
        => TryParse(s, out ZabbixDuration result) ? result
            : throw new FormatException($"'{s}' isn't a length of time Zabbix takes: write a whole number of seconds with an optional s, m, h, d, w, M or y suffix, or a macro.");

    /// <summary>Parses Zabbix's spelling of a length of time, and says whether it is one.</summary>
    /// <param name="s">The length, as Zabbix spells it.</param>
    /// <param name="result">The length, when <paramref name="s"/> is one.</param>
    /// <returns><see langword="true"/> when <paramref name="s"/> is a length, a macro, or empty; otherwise <see langword="false"/>.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, out ZabbixDuration result)
    {
        result = default;
        return s is not null && TryParse(s.AsSpan(), out result);
    }

    /// <summary>Parses Zabbix's spelling of a length of time, and says whether it is one.</summary>
    /// <param name="s">The length, as Zabbix spells it.</param>
    /// <param name="result">The length, when <paramref name="s"/> is one.</param>
    /// <returns><see langword="true"/> when <paramref name="s"/> is a length, a macro, or empty; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, out ZabbixDuration result)
    {
        result = default;
        if (!s.IsEmpty)
        {
            _ = ZabbixIntervalGrammar.LengthWithYear(s, out int length);
            if (length != s.Length) return false;
        }
        result = new(s.ToString());
        return true;
    }

    /// <inheritdoc/>
    static ZabbixDuration IParsable<ZabbixDuration>.Parse(string s, IFormatProvider? provider) => Parse(s);

    /// <inheritdoc/>
    static bool IParsable<ZabbixDuration>.TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ZabbixDuration result) => TryParse(s, out result);

    /// <inheritdoc/>
    static ZabbixDuration ISpanParsable<ZabbixDuration>.Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s);

    /// <inheritdoc/>
    static bool ISpanParsable<ZabbixDuration>.TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out ZabbixDuration result) => TryParse(s, out result);

    /// <summary>Returns the length as Zabbix spells it.</summary>
    /// <returns>The length's text: <c>0</c> for the default value.</returns>
    public override string ToString() => _text ?? "0";

    /// <summary>The length a reply holds, kept as the server wrote it whether or not it fits the grammar.</summary>
    /// <param name="text">The server's text.</param>
    /// <returns>The length.</returns>
    internal static ZabbixDuration FromReply(string text) => new(text);

    /// <summary>
    /// A <see cref="TimeSpan"/> as Zabbix writes a length: whole seconds with the largest suffix that divides them, <c>0</c>
    /// for none.
    /// </summary>
    /// <param name="length">The length of time to write.</param>
    /// <param name="parameter">The name of the caller's parameter, for the exception.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative, isn't a whole number of seconds, or is longer than 2,147,483,647 seconds.</exception>
    internal static string Text(TimeSpan length, string parameter)
    {
        if (length < TimeSpan.Zero || length.Ticks % TimeSpan.TicksPerSecond != 0 || length.Ticks / TimeSpan.TicksPerSecond > int.MaxValue)
            throw new ArgumentOutOfRangeException(parameter, length, "Zabbix takes a length of time in whole seconds, from 0 to 2,147,483,647.");
        long seconds = length.Ticks / TimeSpan.TicksPerSecond;
        (long size, string suffix) = seconds switch
        {
            0 => (1L, string.Empty),
            _ when seconds % ZabbixIntervalGrammar.Week == 0 => (ZabbixIntervalGrammar.Week, "w"),
            _ when seconds % ZabbixIntervalGrammar.Day == 0 => (ZabbixIntervalGrammar.Day, "d"),
            _ when seconds % ZabbixIntervalGrammar.Hour == 0 => (ZabbixIntervalGrammar.Hour, "h"),
            _ when seconds % ZabbixIntervalGrammar.Minute == 0 => (ZabbixIntervalGrammar.Minute, "m"),
            _ => (1L, "s"),
        };
        return (seconds / size).ToString(CultureInfo.InvariantCulture) + suffix;
    }
}

/// <summary>Writes a <see cref="ZabbixDuration"/> as Zabbix spells it, and reads the text, or the number of seconds, a reply holds.</summary>
internal sealed class ZabbixDurationConverter : JsonConverter<ZabbixDuration>
{
    /// <inheritdoc/>
    public override ZabbixDuration Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => ZabbixDuration.FromReply(reader.GetString() ?? string.Empty),
        JsonTokenType.Number when reader.TryGetInt64(out long seconds) => ZabbixDuration.FromReply(seconds.ToString(CultureInfo.InvariantCulture)),
        _ => throw new JsonException($"A length of time is text, not a {reader.TokenType}."),
    };

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ZabbixDuration value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
