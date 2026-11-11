// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using Community.Zabbix;
using Microsoft.Extensions.Configuration;

[assembly: ZabbixPlugin(typeof(ItemPlugin.UptimePlugin))]

namespace ItemPlugin;

/// <summary>What an agent loads from its plugin folder: it adds this plugin's keys to the agent.</summary>
public sealed class UptimePlugin : ZabbixPluginEntry
{
    /// <inheritdoc/>
    public override void Register(IZabbixItemsBuilder items, IConfiguration settings)
        => items.AddItems(new UptimeItems(TimeProvider.System));
}

/// <summary>A set of keys. A library ships this class on its own, and an agent adds it with <c>AddItems</c> on its builder, or <c>agent.Items.Add</c>.</summary>
public sealed class UptimeItems(TimeProvider time) : ZabbixItemProvider
{
    private readonly DateTimeOffset _started = time.GetUtcNow();

    /// <inheritdoc/>
    public override IReadOnlyList<ZabbixItemDefinition> Items { get; } =
        [new("app.uptime", "Seconds since the plugin loaded")];

    /// <inheritdoc/>
    public override ValueTask<ZabbixAgentAnswer> GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken = default)
    {
        double seconds = (time.GetUtcNow() - _started).TotalSeconds;
        return ValueTask.FromResult(ZabbixAgentAnswer.Supported(key, seconds.ToString("0", CultureInfo.InvariantCulture)));
    }
}
