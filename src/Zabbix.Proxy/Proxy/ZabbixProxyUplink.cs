// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Community.Zabbix.Proxy;

/// <summary>
/// Provides the active proxy's side of the server conversation (named apart from the generated
/// <c>ZabbixProxyClient</c>, which is the API's <c>proxy</c> object): pull the configuration (<c>proxy config</c>) and
/// merge it into the one held; push history (<c>proxy data</c>) with ids the caller assigned (the store's sequences) and
/// the <c>proxy_delay</c> of what is left behind; probe a server that has disabled uploads. A façade over a
/// <see cref="ZabbixExchangeCallback"/> — one node or an HA list — with the session the server de-duplicates on and the protocol
/// version it compares.
/// </summary>
/// <remarks>
/// The version is derived, not a literal. It starts at the configured one; a configuration refusal that carries
/// the server's version (a major.minor mismatch always does, on 7.0 and 8.0) makes the uplink announce
/// the server's version from then on and ask once more. A refusal is a failure (<see cref="ZabbixProxyRefusedException"/>),
/// logged at Warning with the server's <c>info</c>, never an empty revision.
/// </remarks>
public sealed class ZabbixProxyUplink
{
    /// <summary>
    /// Gets the protocol version an uplink announces when it's given none, until a server refuses it and names its own:
    /// 7.4.0, the same default as <see cref="ZabbixProxyOptions.Version"/>.
    /// </summary>
    public static Version DefaultVersion { get; } = ZabbixProxyProtocol.Default.ToVersion();

    private readonly ZabbixExchangeCallback _exchange;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ZabbixProxyTelemetry _telemetry;
    private readonly Lock _versionGate = new();
    private ZabbixComponentVersion _version;

    /// <summary>
    /// An uplink for the proxy named <paramref name="proxyName"/> over <paramref name="exchange"/>: it announces
    /// <see cref="DefaultVersion"/> first, in a new session, on the system's clock, and logs nowhere.
    /// </summary>
    /// <param name="exchange">What carries a request to the server and returns its reply, such as a link's <c>AsExchange()</c>.</param>
    /// <param name="proxyName">The proxy's name as the server knows it.</param>
    /// <exception cref="ArgumentException"><paramref name="proxyName"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="exchange"/> or <paramref name="proxyName"/> is <see langword="null"/>.</exception>
    public ZabbixProxyUplink(ZabbixExchangeCallback exchange, string proxyName)
        : this(exchange, proxyName, version: null, session: null, loggers: null, time: null)
    {
    }

    /// <summary>An uplink for the proxy named <paramref name="proxyName"/> over <paramref name="exchange"/>, with the application's logging and clock.</summary>
    /// <param name="exchange">What carries a request to the server and returns its reply, such as a link's <c>AsExchange()</c>.</param>
    /// <param name="proxyName">The proxy's name as the server knows it.</param>
    /// <param name="version">The protocol version to announce first; <see cref="DefaultVersion"/> when <see langword="null"/>.</param>
    /// <param name="session">The session; a new random 32-hex-digit one when <see langword="null"/> (the server refuses any other length).</param>
    /// <param name="loggers">Where to log; nowhere when <see langword="null"/>. The caller keeps owning it.</param>
    /// <param name="time">The clock; the system's when <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="proxyName"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="exchange"/> or <paramref name="proxyName"/> is <see langword="null"/>.</exception>
    public ZabbixProxyUplink(ZabbixExchangeCallback exchange, string proxyName, Version? version, string? session, ILoggerFactory? loggers, TimeProvider? time)
        : this(exchange, proxyName, version is null ? null : ZabbixComponentVersion.FromVersion(version), time, loggers?.CreateLogger<ZabbixProxyUplink>(), session, telemetry: null)
    {
    }

    internal ZabbixProxyUplink(ZabbixExchangeCallback exchange, string proxyName, ZabbixComponentVersion? version, TimeProvider? time, ILogger? logger, string? session, ZabbixProxyTelemetry? telemetry)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyName);
        _exchange = exchange;
        ProxyName = proxyName;
        _version = version ?? ZabbixProxyProtocol.Default;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ZabbixProxyUplink>.Instance;
        _telemetry = telemetry ?? ZabbixProxyTelemetry.Shared;                                // never a meter of its own: an uplink is not disposed
        Session = session ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>The proxy's name.</summary>
    public string ProxyName { get; }

    /// <summary>The session the server de-duplicates history on; new per process unless given, as the upstream proxy's token is.</summary>
    public string Session { get; }

    /// <summary>Gets the protocol version announced now: the one it was given, or the server's once a server refused it and named its own.</summary>
    public Version Version => ProtocolVersion.ToVersion();

    /// <summary>The protocol version announced now, as the protocol compares it.</summary>
    internal ZabbixComponentVersion ProtocolVersion
    {
        get { lock (_versionGate) return _version; }
    }

    /// <summary>
    /// Pulls the configuration: everything when <paramref name="held"/> is at revision 0, a delta otherwise; the reply is
    /// merged into <paramref name="held"/> (<see cref="ZabbixProxyConfig.Merge"/>).
    /// </summary>
    /// <param name="held">The configuration held.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The configuration after the reply; <paramref name="held"/> itself when nothing changed.</returns>
    /// <exception cref="ZabbixProxyRefusedException">The server refused (after adopting its version once, when it named one).</exception>
    public Task<ZabbixProxyConfig> GetConfigAsync(ZabbixProxyConfig held, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(held);
        return GetConfigCoreAsync(held, cancellationToken);
    }

    private async Task<ZabbixProxyConfig> GetConfigCoreAsync(ZabbixProxyConfig held, CancellationToken cancellationToken)
    {
        ZabbixOperation operation = _telemetry.StartConfig(ProxyName, passive: false);
        try
        {
            ZabbixProxyConfig config = await PullAsync(held, adopt: true, cancellationToken).ConfigureAwait(false);
            _telemetry.Finish(operation, extra: new TagList { { "zabbix.proxy.config_revision", config.ConfigRevision } });
            return config;
        }
        catch (Exception failure)
        {
            _telemetry.Finish(operation, failure);
            throw;
        }
    }

    private async Task<ZabbixProxyConfig> PullAsync(ZabbixProxyConfig held, bool adopt, CancellationToken cancellationToken)
    {
        ZabbixComponentVersion version = ProtocolVersion;
        string announced = version.ToString();
        var request = new ZabbixProxyConfigRequest("proxy config", ProxyName, announced, Session, held.ConfigRevision, held.HostmapRevision is 0 ? null : held.HostmapRevision);
        ReadOnlyMemory<byte> reply;
        try
        {
            reply = await _exchange(JsonSerializer.SerializeToUtf8Bytes(request, ZabbixProxyJson.Default.ZabbixProxyConfigRequest), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException silent) when (ZabbixHeader.IsNoReply(silent))
        {
            // The server logs why and sends nothing (zbx_send_proxyconfig); say what that means from here.
            string reason = $"the server closed the connection without answering; it does so when it does not know proxy '{ProxyName}' "
                + "(not created yet, or not yet in its configuration cache: CacheUpdateFrequency) or does not allow its address or encryption; its log says which";
            ProxyLog.ProxyConfigRefused(_logger, ProxyName, announced, null, reason);
            throw new ZabbixProxyRefusedException($"The server refused proxy config: {reason}.", silent);
        }
        using JsonDocument document = JsonDocument.Parse(reply);
        JsonElement root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("response", out JsonElement response) && TextOf(response) != "success")
        {
            string? info = root.TryGetProperty("info", out JsonElement text) ? TextOf(text) : null;
            string? serverVersion = root.TryGetProperty("version", out JsonElement named) ? TextOf(named) : null;
            string reason = info ?? "no reason given";
            ProxyLog.ProxyConfigRefused(_logger, ProxyName, announced, serverVersion, reason);
            if (adopt && ZabbixComponentVersion.TryParse(serverVersion, CultureInfo.InvariantCulture, out ZabbixComponentVersion server) && !ZabbixProxyProtocol.ServesConfig(server, version))
            {
                lock (_versionGate) _version = server;
                string adopted = server.ToString();
                ProxyLog.ProxyVersionAdopted(_logger, ProxyName, adopted, announced, serverVersion);
                return await PullAsync(held, adopt: false, cancellationToken).ConfigureAwait(false);
            }
            throw new ZabbixProxyRefusedException("proxy config", info,
                ZabbixComponentVersion.TryParse(serverVersion, CultureInfo.InvariantCulture, out ZabbixComponentVersion parsed) ? parsed.ToVersion() : null);
        }
        ZabbixProxyConfig merged = held.Merge(root);
        bool fullSync = ZabbixProxyConfig.IsFullSync(root);
        if (!ReferenceEquals(merged, held))
            ProxyLog.ProxyConfigReceived(_logger, ProxyName, merged.ConfigRevision, fullSync, merged.Hosts.Count, merged.Items.Count);
        return merged;
    }

    // A member the protocol says is a string, read whatever the server put there: a refusal whose response, info or
    // version is a number or an object is still a refusal, never a defect that stops the configuration loop.
    private static string? TextOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };

    /// <summary>
    /// Pushes one page — history, discovery, auto-registration, interface availability, task results; the server's answer
    /// says whether it took it and whether uploads are on.
    /// </summary>
    /// <param name="page">The page: whichever of its lists aren't empty.</param>
    /// <param name="backlog">Whether more is held behind this page (upstream's <c>more</c>), so the server expects the next one at once.</param>
    /// <param name="proxyDelay">How long the oldest value left behind has waited (upstream's <c>proxy_delay</c>), in whole seconds; omitted when under one.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The server's answer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="proxyDelay"/> is negative.</exception>
    /// <example>
    /// <code language="csharp">
    /// ZabbixProxyResponse answer = await uplink.UploadAsync(page, ZabbixProxyBacklog.More, TimeSpan.FromSeconds(30));
    /// </code>
    /// </example>
    public Task<ZabbixProxyResponse> UploadAsync(ZabbixProxyDataPage page, ZabbixProxyBacklog backlog, TimeSpan proxyDelay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfLessThan(proxyDelay, TimeSpan.Zero);
        return SendAsync(page, backlog == ZabbixProxyBacklog.More, (long)proxyDelay.TotalSeconds, cancellationToken);
    }

    /// <summary>Asks a server that has disabled uploads whether it has enabled them: a <c>proxy data</c> with every list empty.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The server's answer.</returns>
    public Task<ZabbixProxyResponse> ProbeAsync(CancellationToken cancellationToken = default) => SendAsync(ZabbixProxyDataPage.Empty, more: false, proxyDelay: 0, cancellationToken);

    /// <summary>The upload in the protocol's own terms, for the proxies' send turn.</summary>
    internal async Task<ZabbixProxyResponse> SendAsync(ZabbixProxyDataPage page, bool more, long proxyDelay, CancellationToken cancellationToken)
    {
        ZabbixOperation operation = _telemetry.StartData(ProxyName, passive: false);
        try
        {
            DateTimeOffset now = _time.GetUtcNow();
            IReadOnlyList<ZabbixProxyHistoryItem>? history = page.History is { Count: > 0 } h ? h : null;
            var request = new ZabbixProxyDataRequest("proxy data", ProxyName, Session, history,
                page.Discovery is { Count: > 0 } discovery ? discovery : null,
                page.Autoregistration is { Count: > 0 } autoreg ? autoreg : null,
                page.Availability is { Count: > 0 } availability ? availability : null,
                page.Tasks is { Count: > 0 } tasks ? tasks : null,
                more ? 1 : null, Version, now, proxyDelay > 0 ? proxyDelay : null);
            ReadOnlyMemory<byte> reply = await _exchange(JsonSerializer.SerializeToUtf8Bytes(request, ZabbixProxyJson.Default.ZabbixProxyDataRequest), cancellationToken).ConfigureAwait(false);
            ZabbixProxyResponse response = JsonSerializer.Deserialize(reply.Span, ZabbixProxyJson.Default.ZabbixProxyResponse)
                ?? throw new JsonException("The server answered proxy data with null.");
            if (_logger.IsEnabled(LogLevel.Debug))
                ProxyLog.ProxyDataSent(_logger, ProxyName, history?.Count ?? 0, history is [.., var last] ? last.Sequence : 0, more, response.Response ?? "none", response.Upload ?? "unset");
            _telemetry.Finish(operation, errorType: response.IsSuccess ? null : "refused",
                extra: new TagList { { "zabbix.proxy.values", history?.Count ?? 0 }, { "zabbix.proxy.upload", response.Upload ?? "unset" } });
            return response;
        }
        catch (Exception failure)
        {
            _telemetry.Finish(operation, failure);
            throw;
        }
    }
}
