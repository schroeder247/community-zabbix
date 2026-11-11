// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Community.Zabbix.Listening;

/// <summary>
/// Matches a peer against Zabbix's <c>Server=</c> / <c>AllowedPeers</c> list: the IP addresses, CIDR blocks and DNS names
/// that may connect, read as upstream reads it (<c>zbx_validate_peer_list</c> and <c>zbx_tcp_check_allowed_peers_info</c>
/// in <c>src/libs/zbxcomms/comms.c</c>). What the listeners admit by; the public face of an entry is <see cref="ZabbixPeer"/>,
/// which reads each one, and <see cref="ZabbixListenerOptions.ParseAllowedPeers"/> the list.
/// </summary>
/// <remarks>
/// <para>Standard, IPv4-compatible and IPv4-mapped forms are the same peer. An empty list is an error, as upstream's
/// check fails for one: "everyone" is said explicitly (<c>0.0.0.0/0,::/0</c>), never by omission. A configuration line
/// resolves no macro, so an entry that is one is refused. The list's split at its commas is the only parsing owned here;
/// each entry is a <see cref="ZabbixPeer"/>, its blocks <see cref="IPNetwork"/>.</para>
/// <para>Names are resolved on every check, asynchronously (<see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>),
/// and an unresolved name matches nothing. They are not cached: the BCL does not expose a record's TTL, so a cache
/// would invent a lifetime; the OS resolver caches with the TTL, as it does for upstream's per-check <c>getaddrinfo</c>.</para>
/// </remarks>
internal sealed class ZabbixAllowedPeers : ISpanParsable<ZabbixAllowedPeers>
{
    private readonly IPNetwork[] _networks;
    private readonly string[] _names;

    private ZabbixAllowedPeers(ZabbixPeer[] peers, IPNetwork[] networks, string[] names)
    {
        Peers = peers;
        Entries = [.. peers.Select(peer => peer.ToString())];
        _networks = networks;
        _names = names;
    }

    /// <summary>The entries, in order.</summary>
    public IReadOnlyList<ZabbixPeer> Peers { get; }

    /// <summary>The entries as written, trimmed, in order.</summary>
    public IReadOnlyList<string> Entries { get; }

    /// <summary>The literal blocks: every address and CIDR entry.</summary>
    public IReadOnlyList<IPNetwork> Networks => _networks;

    /// <summary>The DNS names, resolved on each check.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>Whether <paramref name="peer"/> is allowed; names are resolved now, asynchronously.</summary>
    /// <param name="peer">The connecting address.</param>
    /// <param name="cancellationToken">Stops a resolution in progress.</param>
    /// <returns><see langword="true"/> when an entry matches.</returns>
    public ValueTask<bool> ContainsAsync(IPAddress peer, CancellationToken cancellationToken = default)
        => ContainsAsync(peer, Dns.GetHostAddressesAsync, unresolved: null, cancellationToken);

    /// <summary>The check with the resolver and the unresolved-name report the listener supplies.</summary>
    internal ValueTask<bool> ContainsAsync(IPAddress peer, Func<string, CancellationToken, Task<IPAddress[]>> resolve, Action<string, Exception>? unresolved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        foreach (IPNetwork network in _networks)
            if (Matches(network, peer)) return ValueTask.FromResult(true);
        return _names.Length == 0 ? ValueTask.FromResult(false) : ResolveAsync(peer, resolve, unresolved, cancellationToken);
    }

    private async ValueTask<bool> ResolveAsync(IPAddress peer, Func<string, CancellationToken, Task<IPAddress[]>> resolve, Action<string, Exception>? unresolved, CancellationToken cancellationToken)
    {
        foreach (string name in _names)
        {
            IPAddress[] addresses;
            try
            {
                addresses = await resolve(name, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException failure)
            {
                unresolved?.Invoke(name, failure);                                       // upstream: getaddrinfo failed, the entry matches nothing
                continue;
            }
            foreach (IPAddress address in addresses)
                if (Matches(Host(address), peer)) return true;
        }
        return false;
    }

    /// <summary>The list as Zabbix writes it: the entries joined by commas.</summary>
    public override string ToString() => string.Join(',', Entries);

    /// <inheritdoc/>
    public static ZabbixAllowedPeers Parse(string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan(), provider);
    }

    /// <inheritdoc/>
    public static ZabbixAllowedPeers Parse(ReadOnlySpan<char> s, IFormatProvider? provider)
        => TryParse(s, out ZabbixAllowedPeers? peers, out string? error) ? peers : throw new FormatException(error);

    /// <inheritdoc/>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out ZabbixAllowedPeers result)
        => TryParse(s.AsSpan(), provider, out result);

    /// <inheritdoc/>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, [MaybeNullWhen(false)] out ZabbixAllowedPeers result)
        => TryParse(s, out result, out _);

    /// <summary>Parses, saying which entry was refused and why.</summary>
    /// <param name="s">The list.</param>
    /// <param name="result">The list, when it parses.</param>
    /// <param name="error">Why it does not, when it does not.</param>
    internal static bool TryParse(ReadOnlySpan<char> s, [NotNullWhen(true)] out ZabbixAllowedPeers? result, [NotNullWhen(false)] out string? error)
    {
        result = null;
        if (s.IsWhiteSpace())
        {
            error = "the allowed-peer list is empty; Zabbix refuses an empty list (fail closed): name the peers, or say 0.0.0.0/0,::/0 for everyone";
            return false;
        }
        var peers = new List<ZabbixPeer>();
        var networks = new List<IPNetwork>();
        var names = new List<string>();
        foreach (Range range in s.Split(','))
        {
            ReadOnlySpan<char> entry = s[range].Trim();
            if (!ZabbixPeer.TryParse(entry, out ZabbixPeer peer) || (peer.Network is null && peer.HostName is null))
            {
                error = $"\"{entry}\" is not an IP address, a CIDR block or a DNS name";
                return false;
            }
            if (peer.Network is { } network) networks.Add(network);
            else if (peer.HostName is { } name) names.Add(name);
            peers.Add(peer);
        }
        result = new ZabbixAllowedPeers([.. peers], [.. networks], [.. names]);
        error = null;
        return true;
    }

    private static IPNetwork Host(IPAddress address)
        => new(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);

    // Standard, compatible and mapped forms are one peer (upstream zbx_ip_cmp). IPNetwork.Contains already matches an
    // IPv4-mapped peer against an IPv4 block on .NET 10; the compatible form and a v6 block holding the v4
    // peer's mapped or compatible form are added here.
    private static bool Matches(IPNetwork network, IPAddress peer)
    {
        if (network.Contains(peer)) return true;
        bool v4Network = network.BaseAddress.AddressFamily == AddressFamily.InterNetwork;
        if (peer.AddressFamily == AddressFamily.InterNetworkV6 && v4Network)
            return (peer.IsIPv4MappedToIPv6 || IsCompatible(peer)) && network.Contains(new IPAddress(peer.GetAddressBytes().AsSpan(12)));
        if (peer.AddressFamily == AddressFamily.InterNetwork && !v4Network)
            return network.Contains(peer.MapToIPv6()) || network.Contains(Compatible(peer));
        return false;
    }

    private static bool IsCompatible(IPAddress v6)
    {
        Span<byte> bytes = stackalloc byte[16];
        return v6.TryWriteBytes(bytes, out _) && bytes[..12].IndexOfAnyExcept((byte)0) < 0;
    }

    private static IPAddress Compatible(IPAddress v4)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        v4.TryWriteBytes(bytes[12..], out _);
        return new IPAddress(bytes);
    }
}
