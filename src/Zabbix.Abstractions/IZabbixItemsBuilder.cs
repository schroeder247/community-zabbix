// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix;

/// <summary>
/// Represents an agent that answers item keys being set up, in the shape of <c>IHttpClientBuilder</c>: its name and the
/// container it's registered in. The active and the passive agent's builders are one, so a verb written for this interface,
/// such as <c>AddItems</c>, <c>AddItem</c> or <c>AddPlugins</c>, serves both, and a library's registration serves both too.
/// </summary>
/// <remarks>
/// The agent's items are registered under its name. An active and a passive agent of one name in one container answer the
/// same items, as one Zabbix agent answers a key whether the server polls it or the agent sends it.
/// </remarks>
/// <example>
/// A library's registration, generic over the builder so the caller's chain keeps its type:
/// <code>
/// public static TBuilder AddQueueItems&lt;TBuilder&gt;(this TBuilder builder) where TBuilder : IZabbixItemsBuilder
///     => builder.AddItems(services =&gt; new QueueItems(services.GetRequiredService&lt;IQueueReader&gt;()));
/// </code>
/// </example>
public interface IZabbixItemsBuilder
{
    /// <summary>The agent's name: the name of its options, and of a library's options for it.</summary>
    string Name { get; }

    /// <summary>The container the agent is registered in.</summary>
    IServiceCollection Services { get; }
}
