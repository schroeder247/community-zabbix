// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents a host that may connect, as one entry of Zabbix's list of them: an address, a block of addresses, a DNS name
/// or a macro. A trapper item's allowed hosts are a list of them, and so are the peers a listener allows. An
/// <see cref="IPAddress"/> and an <see cref="IPNetwork"/> convert to one,
/// <c>i.TrapperHosts = [IPNetwork.Parse("10.0.0.0/8"), IPAddress.IPv6Loopback]</c>, and <see cref="Parse(string)"/> takes
/// a DNS name or a macro: <c>ZabbixPeer.Parse("monitor.example")</c>.
/// </summary>
/// <remarks>
/// <para>
/// An entry is what the API checks the allowed hosts against (<c>CItemType.php</c> 7.0:195-204, <c>CIPRangeParser.php</c>
/// 7.0): an IPv4 or IPv6 address, one in CIDR notation, a DNS name, a user macro, or one of the host macros
/// <c>{HOST.HOST}</c>, <c>{HOST.NAME}</c>, <c>{HOST.CONN}</c>, <c>{HOST.IP}</c> and <c>{HOST.DNS}</c> (7.0 also takes
/// <c>{HOSTNAME}</c> and <c>{IPADDRESS}</c>), any macro maybe in a macro function. The list goes to the server as Zabbix's
/// comma-separated text, and an empty list lets any host send. <see cref="ParseList(string)"/> reads that text.
/// </para>
/// <para>
/// An IPv6 address's scope isn't sent: Zabbix doesn't take one. Entries are equal when they're written the same, as
/// Zabbix stores what it's given. The default value names no host, and a request that holds it isn't sent. A value read
/// from a reply is kept as the server wrote it, even where a newer Zabbix writes more than this grammar.
/// </para>
/// </remarks>
[JsonConverter(typeof(ZabbixPeerConverter))]
public readonly record struct ZabbixPeer : ISpanParsable<ZabbixPeer>
{
    // The host macros the allowed hosts take, and the two 7.0 still takes under their old names.
    private static readonly ImmutableArray<string> s_hostMacros = ["{HOST.HOST}", "{HOSTNAME}", "{HOST.NAME}", "{HOST.CONN}", "{HOST.IP}", "{IPADDRESS}", "{HOST.DNS}"];

    private readonly string? _text;

    private ZabbixPeer(string text) => _text = text;

    /// <summary>
    /// Gets the addresses the entry allows, as a block: an address is a block of one (<c>/32</c> or <c>/128</c>), and a
    /// CIDR block's address is masked to its prefix, as Zabbix compares it. <see langword="null"/> for a DNS name or a macro.
    /// </summary>
    public IPNetwork? Network => _text is null ? null : NetworkOf(_text);

    /// <summary>Gets the DNS name the entry allows the addresses of; <see langword="null"/> for an address, a block or a macro.</summary>
    public string? HostName => _text is { Length: > 0 } text && text[0] != '{' && NetworkOf(text) is null && IsDnsName(text) ? text : null;

    /// <summary>Converts an address to the entry that allows it.</summary>
    /// <param name="address">The one address the entry allows.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    public static implicit operator ZabbixPeer(IPAddress address) => FromIPAddress(address);

    /// <summary>Converts a block of addresses to the entry that allows it, in CIDR notation.</summary>
    /// <param name="network">The block of addresses the entry allows.</param>
    /// <returns>The entry.</returns>
    public static implicit operator ZabbixPeer(IPNetwork network) => FromIPNetwork(network);

    /// <summary>Creates the entry that allows an address: the named form of the implicit conversion.</summary>
    /// <param name="address">The one address the entry allows.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    public static ZabbixPeer FromIPAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        bool scoped = address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0;
        return new((scoped ? new IPAddress(address.GetAddressBytes()) : address).ToString());
    }

    /// <summary>Creates the entry that allows a block of addresses: the named form of the implicit conversion.</summary>
    /// <param name="network">The block of addresses the entry allows.</param>
    /// <returns>The entry.</returns>
    public static ZabbixPeer FromIPNetwork(IPNetwork network) => new(network.ToString());

    /// <summary>Parses one entry of Zabbix's allowed hosts: an address, a CIDR block, a DNS name or a macro.</summary>
    /// <param name="s">The entry's text, as Zabbix writes it.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="s"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="s"/> isn't an entry Zabbix takes.</exception>
    public static ZabbixPeer Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan());
    }

    /// <summary>Parses one entry of Zabbix's allowed hosts: an address, a CIDR block, a DNS name or a macro.</summary>
    /// <param name="s">The entry's text, as Zabbix writes it.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="FormatException"><paramref name="s"/> isn't an entry Zabbix takes.</exception>
    public static ZabbixPeer Parse(ReadOnlySpan<char> s) => TryParse(s, out ZabbixPeer result) ? result : throw Refused(s);

    /// <summary>Parses one entry of Zabbix's allowed hosts, and says whether it is one.</summary>
    /// <param name="s">The entry's text, as Zabbix writes it.</param>
    /// <param name="result">The entry, when <paramref name="s"/> is one.</param>
    /// <returns><see langword="true"/> when <paramref name="s"/> is an address, a CIDR block, a DNS name or a macro Zabbix takes; otherwise <see langword="false"/>.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, out ZabbixPeer result)
    {
        result = default;
        return s is not null && TryParse(s.AsSpan(), out result);
    }

    /// <summary>Parses one entry of Zabbix's allowed hosts, and says whether it is one.</summary>
    /// <param name="s">The entry's text, as Zabbix writes it.</param>
    /// <param name="result">The entry, when <paramref name="s"/> is one.</param>
    /// <returns><see langword="true"/> when <paramref name="s"/> is an address, a CIDR block, a DNS name or a macro Zabbix takes; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, out ZabbixPeer result)
    {
        result = default;
        s = s.Trim(Spaces);
        if (!IsEntry(s)) return false;
        result = new(s.ToString());
        return true;
    }

    /// <inheritdoc/>
    static ZabbixPeer IParsable<ZabbixPeer>.Parse(string s, IFormatProvider? provider) => Parse(s);

    /// <inheritdoc/>
    static bool IParsable<ZabbixPeer>.TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ZabbixPeer result) => TryParse(s, out result);

    /// <inheritdoc/>
    static ZabbixPeer ISpanParsable<ZabbixPeer>.Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s);

    /// <inheritdoc/>
    static bool ISpanParsable<ZabbixPeer>.TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out ZabbixPeer result) => TryParse(s, out result);

    /// <summary>
    /// Parses Zabbix's allowed hosts: entries separated by commas, each maybe with spaces around it, such as
    /// <c>10.0.0.0/8, ::1, {$TRAPPER_HOSTS}</c>. Empty text is an empty list, which lets any host send.
    /// </summary>
    /// <param name="s">The allowed hosts, as Zabbix writes them.</param>
    /// <returns>The entries, in order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="s"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">An entry isn't one Zabbix takes, or is empty.</exception>
    public static IReadOnlyList<ZabbixPeer> ParseList(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        List<string> entries = Split(s);
        var peers = new ZabbixPeer[entries.Count];
        for (int i = 0; i < peers.Length; i++) peers[i] = TryParse(entries[i], out ZabbixPeer peer) ? peer : throw Refused(entries[i]);
        return peers;
    }

    /// <summary>Returns the entry as Zabbix spells it.</summary>
    /// <returns>The entry's text; empty for the default value.</returns>
    public override string ToString() => _text ?? string.Empty;

    /// <summary>The entries a reply's allowed hosts hold, each kept as the server wrote it whether or not it fits the grammar.</summary>
    /// <param name="text">The server's text.</param>
    /// <returns>The entries, in order.</returns>
    internal static IEnumerable<ZabbixPeer> FromReply(string text) => Split(text).Select(entry => TryParse(entry, out ZabbixPeer peer) ? peer : new(entry));

    /// <summary>The entry's text, or an exception for the default value, which names no host.</summary>
    /// <param name="peer">The entry to write.</param>
    /// <returns>The entry's text.</returns>
    /// <exception cref="ArgumentException"><paramref name="peer"/> is the default value.</exception>
    internal static string Written(ZabbixPeer peer)
        => peer._text ?? throw new ArgumentException("An allowed host names no host: convert one from an IPAddress or an IPNetwork, or parse its text.", nameof(peer));

    private static ReadOnlySpan<char> Spaces => " \t\r\n";

    private static FormatException Refused(ReadOnlySpan<char> entry)
        => new($"'{entry}' isn't an allowed host Zabbix takes: write an IPv4 or IPv6 address, one in CIDR notation, a DNS name, or a macro.");

    // The entries of the list, trimmed: split at each comma that isn't in a macro's context, and a last empty entry
    // after a trailing comma dropped, as the API reads the list (CIPRangeParser.php 7.0).
    private static List<string> Split(ReadOnlySpan<char> s)
    {
        var entries = new List<string>();
        if (s.Trim(Spaces).IsEmpty) return entries;
        int p = 0;
        while (true)
        {
            int start = p;
            while (p < s.Length && Spaces.Contains(s[p])) p++;
            if (p < s.Length && s[p] == '{') p += Macro(s[p..]);
            while (p < s.Length && s[p] != ',') p++;
            entries.Add(s[start..p].Trim(Spaces).ToString());
            if (p >= s.Length) break;
            p++;
        }
        if (entries.Count > 1 && entries[^1].Length == 0) entries.RemoveAt(entries.Count - 1);
        return entries;
    }

    private static int Macro(ReadOnlySpan<char> s) => ZabbixMacroGrammar.UserMacro(s) is var user and > 0 ? user : ZabbixMacroGrammar.BuiltInMacro(s, s_hostMacros);

    private static bool IsEntry(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return false;
        if (s[0] == '{') return Macro(s) == s.Length;
        return NetworkOf(s) is not null || (!s.Contains('/') && IsDnsName(s));
    }

    // An address, or a CIDR block masked to its prefix; null for anything else.
    private static IPNetwork? NetworkOf(ReadOnlySpan<char> s)
    {
        int slash = s.LastIndexOf('/');
        if (slash < 0) return Address(s) is { } address ? new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128) : null;
        ReadOnlySpan<char> prefix = s[(slash + 1)..];
        if (prefix.IsEmpty || prefix.Length > 3 || prefix.ContainsAnyExceptInRange('0', '9') || Address(s[..slash]) is not { } block) return null;
        int bits = int.Parse(prefix, NumberStyles.None, CultureInfo.InvariantCulture);
        bool v4 = block.AddressFamily == AddressFamily.InterNetwork;
        if ((v4 && (prefix.Length > 2 || bits > 32)) || bits > 128) return null;
        Span<byte> bytes = stackalloc byte[v4 ? 4 : 16];
        _ = block.TryWriteBytes(bytes, out _);
        for (int bit = bits; bit < bytes.Length * 8; bit++) bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        return new IPNetwork(new IPAddress(bytes), bits);
    }

    // An IPv4 address of four parts of one to three digits, each at most 255, read in decimal (CIPv4Parser.php 7.0); or an
    // IPv6 address without a scope or brackets, which Zabbix doesn't take.
    private static IPAddress? Address(ReadOnlySpan<char> s)
    {
        if (s.Contains(':'))
            return !s.ContainsAny("%[]") && IPAddress.TryParse(s, out IPAddress? v6) && v6.AddressFamily == AddressFamily.InterNetworkV6 ? v6 : null;
        Span<byte> bytes = stackalloc byte[4];
        int parts = 0;
        foreach (Range range in s.Split('.'))
        {
            ReadOnlySpan<char> part = s[range];
            if (parts == 4 || part.IsEmpty || part.Length > 3 || part.ContainsAnyExceptInRange('0', '9')) return null;
            int value = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
            if (value > 255) return null;
            bytes[parts++] = (byte)value;
        }
        return parts == 4 ? new IPAddress(bytes) : null;
    }

    // Labels of letters, digits, '-' and '_', separated by dots, the first starting with a letter or a digit, and a dot
    // at the end allowed (CDnsParser.php 7.0).
    private static bool IsDnsName(ReadOnlySpan<char> s)
    {
        if (!char.IsAsciiLetterOrDigit(s[0])) return false;
        if (s[^1] == '.') s = s[..^1];
        foreach (Range range in s.Split('.'))
        {
            ReadOnlySpan<char> label = s[range];
            if (label.IsEmpty) return false;
            foreach (char c in label)
                if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) return false;
        }
        return true;
    }
}

/// <summary>Writes a <see cref="ZabbixPeer"/> as Zabbix spells it, and reads the text a reply or a caller's JSON holds.</summary>
internal sealed class ZabbixPeerConverter : JsonConverter<ZabbixPeer>
{
    /// <inheritdoc/>
    public override ZabbixPeer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && ZabbixPeer.FromReply(reader.GetString() ?? string.Empty).ToList() is [var peer]
            ? peer
            : throw new JsonException("An allowed host is the text of one entry.");

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ZabbixPeer value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(ZabbixPeer.Written(value));
    }
}

/// <summary>
/// Writes a list of <see cref="ZabbixPeer"/> as the comma-separated text Zabbix takes, and reads that text, or a JSON
/// list of entries or of such texts, as a caller's JSON may hold.
/// </summary>
internal sealed class ZabbixPeerListConverter : JsonConverter<IReadOnlyList<ZabbixPeer>>
{
    /// <inheritdoc/>
    public override IReadOnlyList<ZabbixPeer> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return [.. ZabbixPeer.FromReply(reader.GetString() ?? string.Empty)];
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("The allowed hosts are text, or a list of entries.");
        var peers = new List<ZabbixPeer>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            peers.AddRange(reader.TokenType == JsonTokenType.String ? ZabbixPeer.FromReply(reader.GetString() ?? string.Empty) : throw new JsonException($"The allowed hosts' list holds a {reader.TokenType}, not an entry."));
        return peers;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<ZabbixPeer> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(string.Join(',', value.Select(ZabbixPeer.Written)));
    }
}
