// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Community.Zabbix.Storage;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Proxy;

/// <summary>
/// Decides which trapped values a proxy takes, by the proxy's own <c>sender_item_validator</c> and
/// <c>agent_item_validator</c> rules (<c>zbxdbwrap/proxy.c</c>); built once per configuration revision.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>the host and the key must name an item of the configuration, the key compared <b>literally</b>
/// (<c>zbx_dc_config_history_recv_get_items_by_keys</c>); the host must be monitored and the item enabled;</item>
/// <item><c>sender data</c> is taken by a <b>trapper</b> item (type 2), or an HTTP agent item (19) with
/// <c>allow_traps</c>; <c>agent data</c> by a Zabbix agent (active) item (7);</item>
/// <item>a trap's peer must be in the item's <c>trapper_hosts</c>, user macros resolved (host, then its templates, then
/// global — 8.0 items default to <c>{$TRAPPER.ALLOWED_HOSTS}</c>, <c>127.0.0.1,::1</c>): IPs, CIDR blocks and DNS names,
/// an IPv4-mapped peer matching its IPv4 form. An empty list admits everyone on a 7.x protocol and no one from 8.0
/// (by the protocol version the server announced).</item>
/// </list>
/// <see cref="Knows"/> is the peer-free part (the store's <c>Admit</c> predicate) and <see cref="Refusal"/> the whole
/// rule for one request. Not modelled: the host's <c>tls_accept</c> against the connection's security (<c>zbx_host_check_permissions</c>) —
/// the listener does not hand a handler the connection's TLS state — and macros other than user macros,
/// <c>{HOST.HOST}</c> and <c>{HOST.NAME}</c>.
/// </remarks>
public sealed partial class ZabbixProxyAdmission
{
    private readonly ZabbixProxyConfig _config;
    private readonly FrozenDictionary<ulong, PeerList> _peers;              // by item id, trap items only

    private ZabbixProxyAdmission(ZabbixProxyConfig config, ZabbixComponentVersion protocol, FrozenDictionary<ulong, PeerList> peers)
    {
        _config = config;
        ProtocolVersion = protocol;
        _peers = peers;
    }

    /// <summary>Nothing is admitted: no configuration yet.</summary>
    public static ZabbixProxyAdmission Empty { get; } = new(ZabbixProxyConfig.Empty, ZabbixProxyProtocol.Default, FrozenDictionary<ulong, PeerList>.Empty);

    /// <summary>The configuration the rule was built from.</summary>
    public ZabbixProxyConfig Config => _config;

    /// <summary>Gets the protocol version whose rules apply.</summary>
    public Version Protocol => ProtocolVersion.ToVersion();

    /// <summary>The protocol version whose rules apply, as the protocol compares it.</summary>
    internal ZabbixComponentVersion ProtocolVersion { get; }

    /// <summary>
    /// Builds the rule for a configuration: resolves every trap item's <c>trapper_hosts</c> (macros, then DNS names)
    /// once, so a trap costs a lookup and a prefix compare.
    /// </summary>
    /// <param name="config">The proxy's configuration, whose trap items' <c>trapper_hosts</c> the rule resolves.</param>
    /// <param name="protocol">The protocol version the proxy speaks.</param>
    /// <param name="resolve">DNS; <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/> when <see langword="null"/>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The rule.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> or <paramref name="protocol"/> is <see langword="null"/>.</exception>
    public static ValueTask<ZabbixProxyAdmission> BuildAsync(ZabbixProxyConfig config, Version protocol, ZabbixPeerResolverCallback? resolve = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(protocol);
        return BuildCoreAsync(config, ZabbixComponentVersion.FromVersion(protocol), resolve, cancellationToken);
    }

    /// <summary>Builds the rule for a configuration, for the protocol version as the protocol compares it.</summary>
    /// <param name="config">The proxy's configuration, whose trap items' <c>trapper_hosts</c> the rule resolves.</param>
    /// <param name="protocol">The protocol version the proxy speaks.</param>
    /// <param name="resolve">DNS; the system's when <see langword="null"/>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The rule.</returns>
    internal static ValueTask<ZabbixProxyAdmission> BuildAsync(ZabbixProxyConfig config, ZabbixComponentVersion protocol, ZabbixPeerResolverCallback? resolve, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        return BuildCoreAsync(config, protocol, resolve, cancellationToken);
    }

    private static async ValueTask<ZabbixProxyAdmission> BuildCoreAsync(ZabbixProxyConfig config, ZabbixComponentVersion protocol, ZabbixPeerResolverCallback? resolve, CancellationToken cancellationToken)
    {
        resolve ??= static async (name, token) =>
        {
            try { return await Dns.GetHostAddressesAsync(name, token).ConfigureAwait(false); }
            catch (SocketException) { return []; }                                        // a name that does not resolve admits no one
        };
        bool strict = ZabbixProxyProtocol.DeniesEmptyTrapperHosts(protocol);
        var byText = new Dictionary<string, PeerList>(StringComparer.Ordinal);
        var peers = new Dictionary<ulong, PeerList>();
        foreach (ZabbixProxyConfigItem item in config.Items.Values)
        {
            if (item.Type is not (ZabbixItemType.Trapper or ZabbixItemType.HttpAgent)) continue;
            if (item.TrapperHosts.Count == 0 && !strict)
            {
                peers[item.ItemId] = PeerList.Everyone;
                continue;
            }
            string written = string.Join(',', item.TrapperHosts);
            string resolved = config.Hosts.TryGetValue(item.HostId, out ZabbixProxyConfigHost? host) ? ResolveMacros(written, host, config) : written;
            if (!byText.TryGetValue(resolved, out PeerList? list))
            {
                list = await PeerList.ParseAsync(resolved, resolve, cancellationToken).ConfigureAwait(false);
                byText[resolved] = list;
            }
            peers[item.ItemId] = list;
        }
        return new ZabbixProxyAdmission(config, protocol, peers.ToFrozenDictionary());
    }

    /// <summary>The peer-free part of the rule: the host and key name an enabled item of a monitored host, of a type that takes values from outside.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether a request could admit it.</returns>
    public bool Knows(ZabbixValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return _config.ItemsByKey.TryGetValue((value.Host, value.Key.ToString()), out ZabbixProxyConfigItem? item)
               && item.Status == ZabbixItemStatus.Active && _config.Hosts[item.HostId].Status == ZabbixHostStatus.Enabled
               && item.Type is ZabbixItemType.Trapper or ZabbixItemType.HttpAgent or ZabbixItemType.ZabbixActive;
    }

    /// <summary>The whole rule for one value of one request: why it is refused, or <see langword="null"/> when it is admitted.</summary>
    /// <param name="value">The value.</param>
    /// <param name="request">The request that carried it: <c>sender data</c> or <c>agent data</c>.</param>
    /// <param name="peer">Who sent it.</param>
    /// <returns>The refusal in Zabbix's words, or <see langword="null"/>.</returns>
    public string? Refusal(ZabbixValue value, string request, IPAddress? peer)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!_config.ItemsByKey.TryGetValue((value.Host, value.Key.ToString()), out ZabbixProxyConfigItem? item)) return "no such host and key in the configuration";
        if (_config.Hosts[item.HostId].Status != ZabbixHostStatus.Enabled) return "the host is not monitored";
        if (item.Status != ZabbixItemStatus.Active) return "the item is disabled";
        if (string.Equals(request, "agent data", StringComparison.Ordinal))
            return item.Type == ZabbixItemType.ZabbixActive ? null : string.Create(CultureInfo.InvariantCulture, $"item type \"{(int)item.Type}\" does not take agent data");
        switch (item.Type)
        {
            case ZabbixItemType.HttpAgent when !item.AllowTraps:
                return "trapping is not enabled";
            case ZabbixItemType.HttpAgent:
            case ZabbixItemType.Trapper:
                break;
            default:
                return string.Create(CultureInfo.InvariantCulture, $"item type \"{(int)item.Type}\" cannot be used with traps");
        }
        return _peers.TryGetValue(item.ItemId, out PeerList? allowed) && allowed.Contains(peer)
            ? null
            : $"connection from \"{peer}\" rejected, allowed hosts: \"{string.Join(',', item.TrapperHosts)}\"";
    }

    /// <summary>
    /// A writer that admits by this rule for one request and passes what it admits to <paramref name="store"/>: the
    /// trapper writes to it, so its <c>processed</c>/<c>failed</c> counts are Zabbix's. The trapper writes through a
    /// <see cref="ChannelWriter{T}"/> and the peer is the request's; the store's <c>Admit</c> predicate sees only the
    /// value, so it can hold the peer-free part (<see cref="Knows"/>) and not <c>trapper_hosts</c>. A writer per request
    /// is the narrowest place the peer and the request kind exist together.
    /// </summary>
    /// <param name="store">Where admitted values go.</param>
    /// <param name="request">The request kind.</param>
    /// <param name="peer">The request's peer.</param>
    /// <param name="refused">Told of each refusal (to log and count it).</param>
    /// <returns>The writer.</returns>
    public ChannelWriter<ZabbixValue> For(ChannelWriter<ZabbixValue> store, string request, IPAddress? peer, Action<ZabbixValue, string>? refused = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new Intake(this, store, request, peer, refused);
    }

    // ----- trapper_hosts -------------------------------------------------------------------------------------------

    [GeneratedRegex(@"\{\$[A-Z0-9_.]+(?::(?:""(?:[^""\\]|\\.)*""|[^}]*))?\}|\{HOST\.(?:HOST|NAME)\}", RegexOptions.CultureInvariant)]
    private static partial Regex MacroPattern();

    /// <summary>Resolves user macros (host, its templates breadth-first, global; a context macro falls back to its base) and <c>{HOST.HOST}</c>/<c>{HOST.NAME}</c>. An unknown macro stays as written, which matches no peer.</summary>
    internal static string ResolveMacros(string text, ZabbixProxyConfigHost host, ZabbixProxyConfig config)
        => MacroPattern().Replace(text, match => match.Value switch
        {
            "{HOST.HOST}" => host.Host,
            "{HOST.NAME}" => host.Name,
            _ => UserMacro(match.Value, host.HostId, config) ?? match.Value,
        });

    private static string? UserMacro(string macro, ulong hostId, ZabbixProxyConfig config)
    {
        int colon = macro.IndexOf(':', StringComparison.Ordinal);
        string? plain = colon < 0 ? null : string.Concat(macro.AsSpan(0, colon), "}");
        foreach (string name in plain is null ? [macro] : new[] { macro, plain })
        {
            var seen = new HashSet<ulong>();
            var queue = new Queue<ulong>([hostId]);
            while (queue.TryDequeue(out ulong id))
            {
                if (!seen.Add(id)) continue;
                if (config.HostMacros.TryGetValue(id, out IReadOnlyDictionary<string, string>? macros) && macros.TryGetValue(name, out string? value)) return value;
                if (config.Templates.TryGetValue(id, out ImmutableArray<ulong> templates)) foreach (ulong template in templates) queue.Enqueue(template);
            }
            if (config.GlobalMacros.TryGetValue(name, out string? global)) return global;
        }
        return null;
    }

    /// <summary>
    /// An item's allowed peers, as <c>zbx_tcp_check_allowed_peers_info</c> reads them: comma-separated, each an address, a
    /// CIDR block or a DNS name (resolved when the rule is built). An empty list admits no one. Parsed here rather than by
    /// <see cref="IPNetwork.Parse(string)"/> alone, which throws on a bare address and a name.
    /// </summary>
    private sealed class PeerList
    {
        private readonly IPNetwork[] _networks;
        private readonly bool _everyone;

        private PeerList(IPNetwork[] networks, bool everyone)
        {
            _networks = networks;
            _everyone = everyone;
        }

        public static PeerList Everyone { get; } = new([], everyone: true);

        // Each entry of the resolved text: an address or a block as itself, anything else a name resolved now; an entry
        // a macro left as it was resolves to no one.
        public static async ValueTask<PeerList> ParseAsync(string text, ZabbixPeerResolverCallback resolve, CancellationToken cancellationToken)
        {
            var networks = new List<IPNetwork>();
            foreach (ZabbixPeer entry in ZabbixPeer.FromReply(text))
            {
                if (entry.Network is { } block) networks.Add(Normalise(block));
                else if (entry.ToString() is { Length: > 0 } name) foreach (IPAddress resolved in await resolve(name, cancellationToken).ConfigureAwait(false)) networks.Add(Single(resolved));
            }
            return new PeerList([.. networks], everyone: false);
        }

        public bool Contains(IPAddress? peer)
        {
            if (_everyone) return true;
            if (peer is null) return false;
            IPAddress address = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
            foreach (IPNetwork network in _networks)
                if (network.Contains(address)) return true;
            return false;
        }

        private static IPNetwork Single(IPAddress address)
        {
            IPAddress plain = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            return new IPNetwork(plain, plain.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
        }

        private static IPNetwork Normalise(IPNetwork block)
            => block.BaseAddress.IsIPv4MappedToIPv6 ? new IPNetwork(block.BaseAddress.MapToIPv4(), Math.Max(0, block.PrefixLength - 96)) : block;
    }

    // The per-request writer: the rule at the door, then the store.
    private sealed class Intake(ZabbixProxyAdmission rule, ChannelWriter<ZabbixValue> store, string request, IPAddress? peer, Action<ZabbixValue, string>? refused)
        : ChannelWriter<ZabbixValue>, IZabbixDurableWriter<ZabbixValue>
    {
        public override bool TryWrite(ZabbixValue item) => Admitted(item) && store.TryWrite(item);

        public ValueTask<bool> TryWriteDurablyAsync(ZabbixValue item, CancellationToken cancellationToken)
            => !Admitted(item) ? ValueTask.FromResult(false)
            : store is IZabbixDurableWriter<ZabbixValue> durable ? durable.TryWriteDurablyAsync(item, cancellationToken)
            : ValueTask.FromResult(store.TryWrite(item));

        private bool Admitted(ZabbixValue item)
        {
            if (rule.Refusal(item, request, peer) is not { } reason) return true;
            refused?.Invoke(item, reason);
            return false;
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => store.WaitToWriteAsync(cancellationToken);

        public override bool TryComplete(Exception? error = null) => false;               // the request does not own the store
    }
}

/// <summary>
/// Logging a refused trap as Zabbix does: a warning naming the item, the host, the peer and the rule; but once per host,
/// key and reason an hour (<see cref="ZabbixProxyTelemetry.RefusedValues"/>), so a sender that repeats a wrong key every
/// second is said once and counted every time.
/// </summary>
internal static class ZabbixProxyAdmissionLogging
{
    public static Action<ZabbixValue, string> Refused(ILogger logger, string proxy, IPAddress? peer, ZabbixProxyTelemetry telemetry)
        => (value, reason) =>
        {
            telemetry.Values(proxy, ZabbixProxyTelemetry.Refused, 1);
            if (!logger.IsEnabled(LogLevel.Warning)) return;
            string key = value.Key.ToString();
            if (telemetry.RefusedValues.ShouldLog((value.Host, key, reason))) ProxyLog.ProxyValueRefused(logger, proxy, value.Host, key, peer, reason);
        };
}
