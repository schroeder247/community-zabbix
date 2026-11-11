// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>Represents the settings of a passive agent: its listener, its host name, and its check timeout.</summary>
public sealed class ZabbixPassiveAgentOptions : ZabbixListenerOptions, IValidatableObject
{
    /// <summary>A passive agent listens on the agent port.</summary>
    public ZabbixPassiveAgentOptions() => Port = ZabbixWireClient.DefaultAgentPort;

    /// <summary>
    /// What this agent is called in its logs, metrics and status when it is constructed directly: <see cref="Hostname"/> when
    /// unset. A registration's name takes its place.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// What <c>agent.hostname</c> answers: the agent's name in a builder, unless set, and the machine's name for an agent
    /// constructed directly.
    /// </summary>
    public string Hostname { get; set; } = Environment.MachineName;

    /// <summary>How long one key may take when the request does not say.</summary>
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

    IEnumerable<ValidationResult> IValidatableObject.Validate(ValidationContext validationContext)
        => Validate(validationContext).Concat(Hosting.ZabbixOptionsRanges.Failures(
            Hosting.ZabbixOptionsRanges.Duration(CheckTimeout, nameof(CheckTimeout)),
            Hosting.ZabbixOptionsRanges.KeyRules(KeyRules, nameof(KeyRules))));
}

/// <summary>The generated validator for <see cref="ZabbixPassiveAgentOptions"/>.</summary>
[OptionsValidator]
internal sealed partial class ZabbixPassiveAgentOptionsValidator : IValidateOptions<ZabbixPassiveAgentOptions>
{
}
