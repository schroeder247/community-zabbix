// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents a port as Zabbix takes it: a number from 0 to 65535, or a macro that holds one, where the field takes a
/// macro. A host interface's, a proxy's, a script's, a user directory's and a media type's SMTP port are one. An
/// <see cref="int"/> converts to one, <c>h.Port = 161</c>, and <see cref="Parse(string)"/> takes a macro:
/// <c>ZabbixPort.Parse("{$SNMP_PORT}")</c>.
/// </summary>
/// <remarks>
/// A macro is a user macro, or an LLD macro, which Zabbix takes in a host prototype's interface (<c>CHostInterface.php</c>
/// and <c>CHostPrototype.php</c> 7.0 check a port so); which fields take a macro is the server's to say. The default value
/// is port 0. A value read from a reply, text or a number, is kept as the server wrote it, even where a newer Zabbix
/// writes more than this grammar.
/// </remarks>
[JsonConverter(typeof(ZabbixPortConverter))]
public readonly record struct ZabbixPort : ISpanParsable<ZabbixPort>
{
    private readonly int _number;

    // A macro, or a reply's text that isn't a number; null for a number.
    private readonly string? _text;

    private ZabbixPort(int number, string? text)
    {
        _number = number;
        _text = text;
    }

    /// <summary>Gets the port's number; <see langword="null"/> when the port is a macro.</summary>
    public int? Number => _text is null ? _number : null;

    /// <summary>Converts a number to the port.</summary>
    /// <param name="number">The port: 0 to 65535.</param>
    /// <returns>The port.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="number"/> isn't from 0 to 65535.</exception>
    public static implicit operator ZabbixPort(int number) => FromInt32(number);

    /// <summary>Creates the port with a number: the named form of the implicit conversion.</summary>
    /// <param name="number">The port: 0 to 65535.</param>
    /// <returns>The port.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="number"/> isn't from 0 to 65535.</exception>
    public static ZabbixPort FromInt32(int number)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, IPEndPoint.MaxPort);
        return new(number, null);
    }

    /// <summary>Parses Zabbix's spelling of a port: a number from 0 to 65535, or a macro, such as <c>{$AGENT_PORT}</c>.</summary>
    /// <param name="s">The port as Zabbix writes it.</param>
    /// <returns>The port.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="s"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="s"/> is neither a port number nor a macro.</exception>
    public static ZabbixPort Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan());
    }

    /// <summary>Parses Zabbix's spelling of a port: a number from 0 to 65535, or a macro, such as <c>{$AGENT_PORT}</c>.</summary>
    /// <param name="s">The port as Zabbix writes it.</param>
    /// <returns>The port.</returns>
    /// <exception cref="FormatException"><paramref name="s"/> is neither a port number nor a macro.</exception>
    public static ZabbixPort Parse(ReadOnlySpan<char> s) => TryParse(s, out ZabbixPort result) ? result : throw new FormatException($"'{s}' isn't a port Zabbix takes: write a number from 0 to 65535, or a macro.");

    /// <summary>Parses Zabbix's spelling of a port, and says whether it is one.</summary>
    /// <param name="s">The port as Zabbix writes it.</param>
    /// <param name="result">The port, when <paramref name="s"/> is one.</param>
    /// <returns><see langword="true"/> when <paramref name="s"/> is a port number or a macro; otherwise <see langword="false"/>.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, out ZabbixPort result)
    {
        result = default;
        return s is not null && TryParse(s.AsSpan(), out result);
    }

    /// <summary>Parses Zabbix's spelling of a port, and says whether it is one.</summary>
    /// <param name="s">The port as Zabbix writes it.</param>
    /// <param name="result">The port, when <paramref name="s"/> is one.</param>
    /// <returns><see langword="true"/> when <paramref name="s"/> is a port number or a macro; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, out ZabbixPort result)
    {
        result = default;
        if (!s.IsEmpty && !s.ContainsAnyExceptInRange('0', '9'))
        {
            if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number > IPEndPoint.MaxPort) return false;
            result = new(number, null);
            return true;
        }
        if (s.IsEmpty || ZabbixMacroGrammar.UserOrLldMacro(s) != s.Length) return false;
        result = new(0, s.ToString());
        return true;
    }

    /// <inheritdoc/>
    static ZabbixPort IParsable<ZabbixPort>.Parse(string s, IFormatProvider? provider) => Parse(s);

    /// <inheritdoc/>
    static bool IParsable<ZabbixPort>.TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ZabbixPort result) => TryParse(s, out result);

    /// <inheritdoc/>
    static ZabbixPort ISpanParsable<ZabbixPort>.Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s);

    /// <inheritdoc/>
    static bool ISpanParsable<ZabbixPort>.TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out ZabbixPort result) => TryParse(s, out result);

    /// <summary>Returns the port as Zabbix spells it: its number, or its macro.</summary>
    /// <returns>The port's text.</returns>
    public override string ToString() => _text ?? _number.ToString(CultureInfo.InvariantCulture);

    /// <summary>The port a reply holds, kept as the server wrote it whether or not it fits the grammar.</summary>
    /// <param name="text">The server's text.</param>
    /// <returns>The port.</returns>
    internal static ZabbixPort FromReply(string text) => TryParse(text, out ZabbixPort port) ? port : new(0, text);
}

/// <summary>Writes a <see cref="ZabbixPort"/> as Zabbix spells it, and reads the text or the number a reply holds.</summary>
internal sealed class ZabbixPortConverter : JsonConverter<ZabbixPort>
{
    /// <inheritdoc/>
    public override ZabbixPort Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => ZabbixPort.FromReply(reader.GetString() ?? string.Empty),
        JsonTokenType.Number when reader.TryGetInt64(out long number) => ZabbixPort.FromReply(number.ToString(CultureInfo.InvariantCulture)),
        _ => throw new JsonException($"A port is a number or text, not a {reader.TokenType}."),
    };

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ZabbixPort value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
