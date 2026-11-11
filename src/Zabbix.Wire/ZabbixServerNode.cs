// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Unicode;

namespace Community.Zabbix;

/// <summary>
/// Represents where a server, proxy or agent listens: a host and a port, as Zabbix's own configuration spells one element
/// of <c>Server</c>/<c>ServerActive</c> (<c>zbx-server</c>, <c>zbx-server:10051</c>, <c>10.0.0.1</c>, <c>fe80::1</c>,
/// <c>[::1]:10051</c>).
/// </summary>
/// <remarks>
/// The grammar is <c>zbx_parse_serveractive_element</c> (<c>src/libs/zbxip/ip.c</c>): an IPv6 address with a port must
/// be bracketed. No BCL type is this grammar: <see cref="IPEndPoint"/> parses only addresses, <see cref="Uri"/> needs a
/// scheme, and <see cref="DnsEndPoint"/> has no parser. So the lexing (brackets, one colon, the port) is here, and the
/// host is judged by the BCL: <see cref="IPAddress.TryParse(ReadOnlySpan{char}, out IPAddress?)"/> for an address and
/// <see cref="Uri.CheckHostName"/> for a name (<c>zbx_server</c> is a name, as upstream's <c>zbx_validate_hostname</c>
/// allows). <see cref="ToDnsEndPoint"/> converts at the socket.
/// </remarks>
internal readonly record struct ZabbixServerNode : ISpanParsable<ZabbixServerNode>, ISpanFormattable, IUtf8SpanFormattable
{
    /// <summary>The trapper port.</summary>
    public const int DefaultPort = 10051;

    /// <summary>A server node at <paramref name="host"/> on the trapper port, <see cref="DefaultPort"/>.</summary>
    /// <param name="host">An IP address (IPv6 without brackets) or a host name.</param>
    /// <exception cref="ArgumentException"><paramref name="host"/> is neither an address nor a host name.</exception>
    public ZabbixServerNode(string host)
        : this(host, DefaultPort)
    {
    }

    /// <summary>A node at <paramref name="host"/>:<paramref name="port"/>.</summary>
    /// <param name="host">An IP address (IPv6 without brackets) or a host name.</param>
    /// <param name="port">The port, 1–65535.</param>
    /// <exception cref="ArgumentException"><paramref name="host"/> is neither an address nor a host name.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is not 1–65535.</exception>
    public ZabbixServerNode(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, IPEndPoint.MinPort + 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);
        if (!IsHost(host))
        {
            // SendTo("zabbix.example:10051") is the natural first try; say what to do instead of only what is wrong.
            throw new ArgumentException(TryParse(host, null, out ZabbixServerNode written)
                ? $"'{host}' is a host and a port: pass the host '{written.Host}' and the port {written.Port} separately, or read the text with {nameof(ZabbixServerNode)}.{nameof(Parse)}"
                : $"'{host}' is neither an IP address nor a host name", nameof(host));
        }
        Host = host;
        Port = port;
    }

    /// <summary>The host name or address; empty for <see langword="default"/>.</summary>
    public string Host { get => field ?? string.Empty; }

    /// <summary>The port.</summary>
    public int Port { get; }

    /// <summary>The node as the public API carries it.</summary>
    /// <param name="node">The node to convert.</param>
    public static implicit operator DnsEndPoint(ZabbixServerNode node) => node.ToDnsEndPoint();

    /// <summary>The node a public endpoint names.</summary>
    /// <param name="endPoint">The endpoint to read.</param>
    public static implicit operator ZabbixServerNode(DnsEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        return new(endPoint.Host, endPoint.Port);
    }

    /// <summary>The endpoint a socket connects to: <see cref="Host"/> is resolved there, every address tried in turn.</summary>
    /// <returns>The node's endpoint, for a socket to connect to.</returns>
    public DnsEndPoint ToDnsEndPoint() => new(Host, Port);

    /// <inheritdoc/>
    public static ZabbixServerNode Parse(string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan(), provider);
    }

    /// <inheritdoc/>
    public static ZabbixServerNode Parse(ReadOnlySpan<char> s, IFormatProvider? provider)
        => TryParse(s, provider, out ZabbixServerNode node) ? node : throw new FormatException($"'{s}' is not a server node: expected host, host:port, an IPv6 address, or [ipv6]:port.");

    /// <inheritdoc/>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ZabbixServerNode result) => TryParse(s.AsSpan(), provider, out result);

    /// <inheritdoc/>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out ZabbixServerNode result) => TryParse(s, DefaultPort, out result);

    /// <summary>
    /// Reads <paramref name="s"/> as <see cref="Parse(string, IFormatProvider?)"/> does, with <paramref name="defaultPort"/>
    /// where it names no port: a client's string constructor, whose peer has its own default port (an agent's is 10050).
    /// </summary>
    /// <param name="s">A host, <c>host:port</c>, an IPv6 address, or <c>[ipv6]:port</c>.</param>
    /// <param name="defaultPort">The port when <paramref name="s"/> names none.</param>
    /// <param name="paramName">The caller's parameter, for the exception.</param>
    /// <exception cref="ArgumentException"><paramref name="s"/> is empty or is not a node.</exception>
    internal static ZabbixServerNode Parse(string s, int defaultPort, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(s, paramName);
        return TryParse(s.AsSpan(), defaultPort, out ZabbixServerNode node)
            ? node
            : throw new ArgumentException($"'{s}' is not a server node: expected host, host:port, an IPv6 address, or [ipv6]:port.", paramName);
    }

    internal static bool TryParse(ReadOnlySpan<char> s, int defaultPort, out ZabbixServerNode result)
    {
        result = default;
        s = s.Trim();
        if (s.IsEmpty) return false;

        if (s[0] == '[')                                                                  // [ipv6] or [ipv6]:port
        {
            int close = s.IndexOf(']');
            if (close < 0) return false;
            ReadOnlySpan<char> address = s[1..close];
            ReadOnlySpan<char> rest = s[(close + 1)..];
            if (!IPAddress.TryParse(address, out IPAddress? v6) || v6.AddressFamily != AddressFamily.InterNetworkV6) return false;
            int bracketPort = defaultPort;
            if (!rest.IsEmpty && (rest[0] != ':' || !TryParsePort(rest[1..], out bracketPort))) return false;
            result = new ZabbixServerNode(new string(address), bracketPort);
            return true;
        }

        if (IPAddress.TryParse(s, out IPAddress? bare) && bare.AddressFamily == AddressFamily.InterNetworkV6)
        {
            result = new ZabbixServerNode(new string(s), defaultPort);                   // a bare IPv6 address takes no port
            return true;
        }

        int colon = s.IndexOf(':');
        if (colon >= 0 && s[(colon + 1)..].Contains(':')) return false;                 // two colons and no brackets: not host:port
        int port = defaultPort;
        if (colon >= 0 && !TryParsePort(s[(colon + 1)..], out port)) return false;
        string host = new(colon < 0 ? s : s[..colon]);
        if (!IsHost(host)) return false;
        result = new ZabbixServerNode(host, port);
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => IsV6 ? string.Create(CultureInfo.InvariantCulture, $"[{Host}]:{Port}") : string.Create(CultureInfo.InvariantCulture, $"{Host}:{Port}");

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        ZabbixFormat.ThrowIfNotGeneral(format, nameof(ZabbixServerNode));
        return ToString();
    }

    /// <inheritdoc/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        ZabbixFormat.ThrowIfNotGeneral(format, nameof(ZabbixServerNode));
        return IsV6
            ? destination.TryWrite(CultureInfo.InvariantCulture, $"[{Host}]:{Port}", out charsWritten)
            : destination.TryWrite(CultureInfo.InvariantCulture, $"{Host}:{Port}", out charsWritten);
    }

    /// <inheritdoc/>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        ZabbixFormat.ThrowIfNotGeneral(format, nameof(ZabbixServerNode));
        return IsV6
            ? Utf8.TryWrite(utf8Destination, CultureInfo.InvariantCulture, $"[{Host}]:{Port}", out bytesWritten)
            : Utf8.TryWrite(utf8Destination, CultureInfo.InvariantCulture, $"{Host}:{Port}", out bytesWritten);
    }

    private bool IsV6 => Host.Contains(':', StringComparison.Ordinal);

    private static bool IsHost(string host) => IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns;

    private static bool TryParsePort(ReadOnlySpan<char> text, out int port)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > IPEndPoint.MinPort and <= IPEndPoint.MaxPort;
}
