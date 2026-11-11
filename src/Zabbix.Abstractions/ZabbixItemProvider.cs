// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

namespace Community.Zabbix;

/// <summary>
/// Provides a set of item keys an agent answers: what each key measures, and its value when the agent is asked. An
/// abstract class (an extension point that may gain members), so a library implements keys against
/// <c>Community.Zabbix.Abstractions</c> alone, and an agent takes them from code (<c>AddItems</c> on its builder, or
/// <c>Items.Add</c> while it runs) or from a plugin.
/// </summary>
/// <remarks>
/// <para>An agent's table reads <see cref="Items"/> once, when the provider is added, and the agent calls
/// <see cref="GetValueAsync"/> only for the key names it lists, with the whole key and its parameters. The agent's own rules
/// hold for every key: the check's timeout reaches the cancellation token, an exception is answered as not supported with
/// its message, and checks run concurrently, so you must make an implementation thread-safe.</para>
/// <para>A provider's keys follow the rule of an agent's <c>Items</c>: <c>Add</c> refuses a name held, all of the provider's
/// keys or none, and the indexer replaces. In a registration, providers are added first and each <c>AddItem</c> then
/// replaces. At start, the keys every agent answers and the bundled items fill the names left free. Two providers of one
/// agent cannot list one key name. A provider registered by type, or by a factory, is built by the agent's container and
/// disposed with it; one added as an instance stays the caller's.</para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class QueueItems(IQueueReader queues) : ZabbixItemProvider
/// {
///     public override IReadOnlyList&lt;ZabbixItemDefinition&gt; Items { get; } =
///         [new("app.queue.depth", "Messages waiting in a queue") { TestKey = "app.queue.depth[orders]" }];
///
///     public override async ValueTask&lt;ZabbixAgentAnswer&gt; GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken = default)
///         => ZabbixAgentAnswer.Supported(key, (await queues.DepthAsync(key[0], cancellationToken)).ToString(CultureInfo.InvariantCulture));
/// }
/// </code>
/// </example>
public abstract class ZabbixItemProvider
{
    /// <summary>Gets the keys this provider answers, by name; an agent asks it only for these.</summary>
    public abstract IReadOnlyList<ZabbixItemDefinition> Items { get; }

    /// <summary>Answers one key: its value, or why it is not supported.</summary>
    /// <param name="key">The key asked for, its parameters included; its name is one of <see cref="Items"/>.</param>
    /// <param name="cancellationToken">The check's deadline.</param>
    /// <returns>The answer (<see cref="ZabbixAgentAnswer.Supported"/> or <see cref="ZabbixAgentAnswer.NotSupported"/>).</returns>
    public abstract ValueTask<ZabbixAgentAnswer> GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken = default);
}
