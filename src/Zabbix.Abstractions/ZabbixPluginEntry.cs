// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Configuration;

namespace Community.Zabbix;

/// <summary>
/// The entry of a plugin an agent loads from a folder (<c>Community.Zabbix.Agent.Plugins</c>): it registers the
/// plugin's providers in the agent, as a library's compile-time registration does, so one library ships both ways.
/// The plugin's assembly names it with <see cref="ZabbixPluginAttribute"/>; it is created with its public parameterless
/// constructor, once, while the agent's registration runs.
/// </summary>
/// <example>
/// <code>
/// [assembly: ZabbixPlugin(typeof(QueuePlugin))]
///
/// public sealed class QueuePlugin : ZabbixPluginEntry
/// {
///     public override void Register(IZabbixItemsBuilder items, IConfiguration settings)
///     {
///         items.Services.AddSingleton&lt;IQueueReader&gt;(new QueueReader(settings["ConnectionString"]));
///         items.AddItems&lt;QueueItems&gt;();
///     }
/// }
/// </code>
/// </example>
public abstract class ZabbixPluginEntry
{
    /// <summary>Registers the plugin's providers, and what they need, in the agent's container.</summary>
    /// <param name="items">The agent.</param>
    /// <param name="settings">The plugin's own configuration section (<c>Plugins:{name}</c>), empty when there is none.</param>
    public abstract void Register(IZabbixItemsBuilder items, IConfiguration settings);
}
