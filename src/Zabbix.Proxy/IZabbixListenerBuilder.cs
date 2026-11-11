// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix;

/// <summary>
/// Represents a builder of something that listens: a <see cref="Listening.ZabbixListener"/>, a passive agent, or a proxy,
/// whose trapper is its listener. <see cref="ZabbixListenerBuilderExtensions"/> holds the verbs that say who may connect,
/// <c>AllowPeer</c> and <c>AllowPsk</c>, so each is written once for every such builder.
/// </summary>
/// <remarks>
/// It has the name and the container every builder has, as <c>IHttpClientBuilder</c> does.
/// <see cref="Listening.ZabbixListenerBuilder"/>, <see cref="IZabbixPassiveAgentBuilder"/> and <see cref="IZabbixProxyBuilder"/>
/// say where their listener's options are. Only those builders implement it.
/// </remarks>
public interface IZabbixListenerBuilder
{
    /// <summary>The name of what listens: the name of its options, and the key it's registered under.</summary>
    string Name { get; }

    /// <summary>The container it's registered in.</summary>
    IServiceCollection Services { get; }

    /// <summary>Sets the listener's options, in order with the builder's other verbs.</summary>
    /// <param name="configure">Sets the options.</param>
    internal void ConfigureListener(Action<ZabbixListenerOptions> configure);
}
