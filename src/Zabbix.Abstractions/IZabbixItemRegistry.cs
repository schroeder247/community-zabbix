// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

namespace Community.Zabbix;

/// <summary>
/// Represents the keys of a running agent, as a library's registration or a plugin adds to them. An agent's <c>Items</c> is
/// one, so a registration written for <see cref="IZabbixItemsBuilder"/> also serves an agent the application constructed
/// itself.
/// </summary>
public interface IZabbixItemRegistry
{
    /// <summary>
    /// Runs <paramref name="configure"/> and adds the providers and keys it registers. What it registers, and what those
    /// providers need, is released with the agent.
    /// </summary>
    /// <param name="configure">The registration: a library's, or a plugin's entry.</param>
    /// <returns>This registry.</returns>
    /// <example>
    /// <code>agent.Items.Register(items => items.AddQueueItems());</code>
    /// </example>
    IZabbixItemRegistry Register(Action<IZabbixItemsBuilder> configure);
}
