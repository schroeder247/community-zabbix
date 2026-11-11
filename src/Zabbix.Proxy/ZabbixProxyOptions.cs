// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel.DataAnnotations;
using System.Threading.Channels;
using Community.Zabbix.Proxy;
using Community.Zabbix.Storage;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Represents the options of one proxy. A builder's verbs write them as named options, and a file's <c>Proxies:{name}</c>
/// section binds them. The name is the proxy's name as the server knows it. Each knob is the upstream proxy's configuration
/// parameter of the same meaning, with its default. The public members are the keys; a chosen store, the providers and the
/// handlers are what only code can give, through the builder's verbs. <see cref="Mode"/> is read only by
/// <see cref="ZabbixProxiesServiceCollectionExtensions.AddZabbixProxies"/>, to choose the verb; code that calls
/// <see cref="ZabbixActiveProxyServiceCollectionExtensions.AddZabbixActiveProxy"/> or
/// <see cref="ZabbixPassiveProxyServiceCollectionExtensions.AddZabbixPassiveProxy"/> directly has already chosen and the value is ignored.
/// </summary>
public sealed class ZabbixProxyOptions : IValidatableObject
{
    /// <summary>
    /// The proxy's name as the server knows it, required when the proxy is constructed directly. A registration's name
    /// takes its place.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Which verb a document row wires to; see <see cref="ZabbixProxyRole"/>. Ignored outside <see cref="ZabbixProxiesServiceCollectionExtensions.AddZabbixProxies"/>.</summary>
    public ZabbixProxyRole Mode { get; set; } = ZabbixProxyRole.Active;

    /// <summary>
    /// The server an active proxy reports to, as Zabbix's <c>Server</c> spells it: <c>host</c>, <c>host:port</c> or
    /// <c>[ipv6]:port</c>, on port 10051 when it names none, or the nodes of one HA cluster joined by <c>;</c>, tried in
    /// order. A proxy reports to one server or one cluster, so a <c>,</c> is refused, as Zabbix's proxy refuses it. A
    /// builder's <c>AddServer</c> sets it.
    /// </summary>
    public string? Server { get; set; }

    /// <inheritdoc cref="ZabbixConnectionOptions"/>
    [ValidateObjectMembers(typeof(Protocol.ZabbixConnectionOptionsValidator))]
    public ZabbixConnectionOptions Connection { get; set; } = new();

    /// <summary>
    /// The protocol version announced: <c>major.minor[.patch]</c>. A server serves configuration only to its own
    /// major.minor. An active proxy announces <c>7.4.0</c> when this is not set and
    /// adopts the version a refusing server names. A passive proxy must have it set, to the server's: no server message
    /// says its version to a passive proxy, and one of another version is rated unsupported or outdated and never
    /// configured (<c>proxy.c</c> <c>zbx_get_proxy_compatibility</c>, 8.0:3007-3031), so
    /// <see cref="ZabbixPassiveProxyServiceCollectionExtensions.AddZabbixPassiveProxy"/> fails at start without it.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>How often an active proxy pulls its configuration; Zabbix's <c>ProxyConfigFrequency</c>.</summary>
    public TimeSpan ConfigInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often an active proxy sends history when there is no <c>more</c>; Zabbix's <c>DataSenderFrequency</c>.</summary>
    public TimeSpan DataInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How many values one page carries at most; upstream's <c>ZBX_MAX_HRECORDS</c>.</summary>
    [Range(1, 100_000)]
    public int PageSize { get; set; } = 1000;

    /// <summary>
    /// How long history is kept when it cannot be sent; Zabbix's <c>ProxyOfflineBuffer</c> (1 to 720 hours, default 1). A
    /// stored value older than this is dropped (counted <c>expired</c>) instead of sent.
    /// </summary>
    public TimeSpan OfflineBuffer { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long a stopping proxy keeps trying to send what it holds: the loss window at shutdown for a memory store.</summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The store's item cap; <see cref="ZabbixStorePolicy{T}.DefaultMaxItems"/> when unset.</summary>
    [Range(1, int.MaxValue)]
    public int? MaxItems { get; set; }

    /// <inheritdoc cref="ZabbixStorePolicy{T}.Overflow"/>
    public BoundedChannelFullMode Overflow { get; set; } = BoundedChannelFullMode.DropOldest;

    /// <summary>The listener: the trapper agents and senders reach the proxy on, and for a passive proxy the server too.</summary>
    [ValidateObjectMembers]
    public ZabbixListenerOptions Trapper { get; set; } = new();

    /// <summary>A store other than the in-memory default; code only.</summary>
    internal Func<IServiceProvider, ZabbixStorePolicy<ZabbixValue>, ZabbixStoreServices, ZabbixStore<ZabbixValue>>? Storage { get; set; }

    /// <summary>The data providers asked for a page before each upload, in the order they were added; code only.</summary>
    internal List<ZabbixProxyDataCallback> Providers { get; } = [];

    /// <summary>What runs a task the server hands back; code only. Without it, tasks are logged.</summary>
    internal ZabbixProxyTaskCallback? TaskHandler { get; set; }

    /// <summary>What may veto a configuration the server pushes to a passive proxy; code only. Without it, every one is taken.</summary>
    internal ZabbixPassiveProxyConfigCallback? ConfigHandler { get; set; }

    /// <summary>What answers <c>zabbix.stats</c> on a passive proxy; code only. Without it, the reply's <c>data</c> is empty.</summary>
    internal ZabbixProxyStatsCallback? StatsHandler { get; set; }

    /// <summary>DNS for the names in items' allowed hosts; code only (tests).</summary>
    internal ZabbixPeerResolverCallback? Resolve { get; set; }

    /// <summary>Why a passive proxy without <see cref="Version"/> is refused, as the validator and the host say it.</summary>
    internal const string PassiveVersionRequired = "Version is required for a passive proxy: set it to the server's version (major.minor[.patch]); the server never tells a passive proxy its own, and one of another major.minor is never configured";

    /// <summary>The version, parsed; valid once the options are validated.</summary>
    internal ZabbixComponentVersion ProtocolVersion => Version is null ? ZabbixProxyProtocol.Default
        : ZabbixComponentVersion.TryParse(Version, System.Globalization.CultureInfo.InvariantCulture, out ZabbixComponentVersion version) ? version : throw new InvalidOperationException($"The version '{Version}' is not major.minor[.patch].");

    /// <summary>The store's policy: the cap, the overflow, the offline buffer's age and the admission rule's peer-free part.</summary>
    internal ZabbixStorePolicy<ZabbixValue> StoragePolicy(Func<ZabbixValue, bool> admit) => new()
    {
        MaxItems = MaxItems ?? ZabbixStorePolicy<ZabbixValue>.DefaultMaxItems,
        Overflow = Overflow,
        MaxAge = OfflineBuffer,
        Admit = admit,
    };

    /// <summary>
    /// The store's policy for a kind that has no admission rule of its own (discovery, auto-registration, interface
    /// availability, task results): the same cap, overflow and offline-buffer age as <see cref="StoragePolicy"/>, with
    /// every record admitted.
    /// </summary>
    internal ZabbixStorePolicy<T> SimpleStoragePolicy<T>() => new()
    {
        MaxItems = MaxItems ?? ZabbixStorePolicy<T>.DefaultMaxItems,
        Overflow = Overflow,
        MaxAge = OfflineBuffer,
    };

    IEnumerable<ValidationResult> IValidatableObject.Validate(ValidationContext validationContext)
        => Hosting.ZabbixOptionsRanges.Failures(
            Hosting.ZabbixOptionsRanges.Duration(ConfigInterval, nameof(ConfigInterval)),
            Hosting.ZabbixOptionsRanges.Duration(DataInterval, nameof(DataInterval)),
            Hosting.ZabbixOptionsRanges.Duration(OfflineBuffer, nameof(OfflineBuffer), longest: TimeSpan.FromHours(720)),
            Hosting.ZabbixOptionsRanges.Duration(DrainTimeout, nameof(DrainTimeout), zeroAllowed: true));
}

/// <summary>The generated validator for <see cref="ZabbixProxyOptions"/>.</summary>
[OptionsValidator]
internal sealed partial class ZabbixProxyOptionsValidator : IValidateOptions<ZabbixProxyOptions>
{
}
