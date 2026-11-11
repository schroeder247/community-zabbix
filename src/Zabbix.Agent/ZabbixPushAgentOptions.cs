// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel.DataAnnotations;
using System.Threading.Channels;
using Community.Zabbix.Storage;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Represents the settings of a push agent. A builder's verbs write them as named options, and a file's
/// <c>PushAgents:{name}</c> section binds them. The public members are the keys; the sources and a chosen store are what
/// only code can give, through the builder's verbs.
/// </summary>
public sealed class ZabbixPushAgentOptions
{
    /// <summary>
    /// What this agent is called in its logs, metrics and status when it is constructed directly: the machine's name when unset. A
    /// registration's name takes its place.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Where the agent sends, in Zabbix's <c>ServerActive</c> grammar: <c>zbx1;zbx2</c> is
    /// one HA destination tried in order, <c>zbx1,proxy-b</c> two independent destinations that each get every value, over
    /// their own store and connection. Each element is <c>host</c>, <c>host:port</c> or <c>[ipv6]:port</c>. A builder's
    /// <c>AddServer</c> adds one destination to it.
    /// </summary>
    public string? ServerActive { get; set; }

    /// <summary>
    /// The host, as Zabbix knows it, that typed polls and meters report for: the agent's name, in a builder, unless set. A
    /// <see cref="ZabbixValue"/> names its own host.
    /// </summary>
    public string? Hostname { get; set; }

    /// <inheritdoc cref="ZabbixConnectionOptions"/>
    [ValidateObjectMembers(typeof(Protocol.ZabbixConnectionOptionsValidator))]
    public ZabbixConnectionOptions Connection { get; set; } = new();

    /// <summary>The store's item cap; <see cref="ZabbixStorePolicy{T}.DefaultMaxItems"/> when unset.</summary>
    [Range(1, int.MaxValue)]
    public int? MaxItems { get; set; }

    /// <inheritdoc cref="ZabbixStorePolicy{T}.Overflow"/>
    public BoundedChannelFullMode Overflow { get; set; } = BoundedChannelFullMode.DropOldest;

    /// <inheritdoc cref="ZabbixSendOptions"/>
    [ValidateObjectMembers]
    public ZabbixSendOptions Send { get; set; } = new();

    /// <summary>What produces records; code only.</summary>
    internal List<Func<IServiceProvider, IAsyncEnumerable<ZabbixValue>>> Sources { get; } = [];

    /// <summary>A store other than the in-memory default, built per destination from the agent's policy and services; code only.</summary>
    internal Func<IServiceProvider, ZabbixStorePolicy<ZabbixValue>, ZabbixStoreServices, ZabbixStore<ZabbixValue>>? Storage { get; set; }

    /// <summary>Whether a source reports for <see cref="Hostname"/> (a typed poll, a meter), which the validator then requires.</summary>
    internal bool ReportsForHost { get; set; }

    /// <summary>The services the polls read, by the item that reads each: the agent's container must have them when it's built.</summary>
    internal List<(Type Service, string KeyName)> Requires { get; } = [];
}

/// <summary>The generated validator for <see cref="ZabbixPushAgentOptions"/>.</summary>
[OptionsValidator]
internal sealed partial class ZabbixPushAgentOptionsValidator : IValidateOptions<ZabbixPushAgentOptions>
{
}
