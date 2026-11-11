// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel.DataAnnotations;
using System.Threading.Channels;
using Community.Zabbix.Hosting;
using Community.Zabbix.Protocol;
using Community.Zabbix.Storage;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>Represents the settings of one active agent. A builder's verbs write them as named options, and
/// <see cref="ZabbixAgentsServiceCollectionExtensions.AddZabbixAgents"/> binds them from the document's <c>ActiveAgents:{name}</c>
/// row; <c>AddZabbixActiveAgent</c> alone binds nothing (call <c>Bind(section)</c> or <c>Configure</c> on its builder).</summary>
public sealed class ZabbixActiveAgentOptions : IValidatableObject
{
    /// <summary>
    /// What this agent is called in its logs, metrics and status when it is constructed directly: <see cref="Hostname"/> when unset. A
    /// registration's name takes its place.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The host name as the server knows it: the agent's name in a builder, unless set, and the machine's name for an agent
    /// constructed directly.
    /// </summary>
    public string Hostname { get; set; } = Environment.MachineName;

    /// <summary>What the server auto-registers the host by, when it does.</summary>
    public string? HostMetadata { get; set; }

    /// <summary>
    /// Where the agent talks to, in Zabbix's <c>ServerActive</c> grammar: <c>zbx1;zbx2</c>
    /// is one HA destination, <c>zbx1,proxy-b</c> two independent destinations, each with its own item list, session,
    /// buffer and heartbeat — upstream starts one active-checks thread per destination. A builder's <c>AddServer</c> adds one
    /// destination to it.
    /// </summary>
    public string? ServerActive { get; set; }

    /// <inheritdoc cref="ZabbixConnectionOptions"/>
    [Microsoft.Extensions.Options.ValidateObjectMembers(typeof(ZabbixConnectionOptionsValidator))]
    public ZabbixConnectionOptions Connection { get; set; } = new();

    /// <summary>How often the item list is refreshed; Zabbix's <c>RefreshActiveChecks</c>.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a heartbeat goes; Zabbix's <c>HeartbeatFrequency</c>: whole seconds, 1 to 3600. Zero turns it off.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long one check may take when the server does not say.</summary>
    public TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Which keys the agent answers, as lines of Zabbix's agent configuration: <c>AllowKey=&lt;pattern&gt;</c> and
    /// <c>DenyKey=&lt;pattern&gt;</c>, tried in order, the first that matches deciding (<see cref="ZabbixKeyRules"/>).
    /// None by default: every key but <c>system.run</c>, as in Zabbix's agents.
    /// </summary>
    public IList<string> KeyRules { get; } = [];

    /// <summary>
    /// Whether the agent answers the bundled items, Zabbix's own system, process, file, network and web keys, beside the
    /// four every agent answers (docs/guides/bundled-items.md); on by default. A handler or a provider of the same name
    /// is answered instead.
    /// </summary>
    public bool BundledItems { get; set; } = true;

    /// <summary>
    /// How many checks of one round run at once; four per processor by default. Upstream's agent 2 runs a bounded pool of
    /// plugin workers too: ten thousand items falling due together must not start ten thousand handlers.
    /// </summary>
    [Range(1, 65_536)]
    public int MaxConcurrentChecks { get; set; } = Math.Max(4, 4 * Environment.ProcessorCount);

    /// <summary>The store's item cap; <see cref="ZabbixStorePolicy{T}.DefaultMaxItems"/> when unset.</summary>
    [Range(1, int.MaxValue)]
    public int? MaxItems { get; set; }

    /// <summary>
    /// What a full store sheds (<see cref="BoundedChannelFullMode.DropOldest"/> by default). <see cref="BoundedChannelFullMode.Wait"/>
    /// is refused: checks run on their schedule, and a slow server must cost counted drops, never a late check.
    /// </summary>
    public BoundedChannelFullMode Overflow { get; set; } = BoundedChannelFullMode.DropOldest;

    /// <summary>How values are batched and sent; <c>FlushInterval</c> is Zabbix's <c>BufferSend</c>.</summary>
    [ValidateObjectMembers]
    public ZabbixSendOptions Send { get; set; } = new();

    IEnumerable<ValidationResult> IValidatableObject.Validate(ValidationContext validationContext)
        => ZabbixOptionsRanges.Failures(
            ZabbixOptionsRanges.Duration(RefreshInterval, nameof(RefreshInterval)),
            ZabbixOptionsRanges.Duration(HeartbeatInterval, nameof(HeartbeatInterval), zeroAllowed: true, longest: TimeSpan.FromHours(1)),
            ZabbixOptionsRanges.Duration(CheckTimeout, nameof(CheckTimeout)),
            ZabbixOptionsRanges.KeyRules(KeyRules, nameof(KeyRules)));
}

/// <summary>The generated validator for <see cref="ZabbixActiveAgentOptions"/>.</summary>
[OptionsValidator]
internal sealed partial class ZabbixActiveAgentOptionsValidator : IValidateOptions<ZabbixActiveAgentOptions>
{
}
