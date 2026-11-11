// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix;

/// <summary>
/// Represents a passive agent being set up, in the shape of <c>IHttpClientBuilder</c>: its name and the container it's
/// registered in, which its item builder and its listener builder share. <see cref="ZabbixPassiveAgentBuilderExtensions"/> holds its own
/// verbs, the item verbs (<c>AddItem</c>, <c>AddItems</c>, <c>AddPlugins</c>) are the ones an active agent has, and the
/// listener's verbs (<c>AllowPeer</c>, <c>AllowPsk</c>) the ones a proxy has.
/// <see cref="ZabbixPassiveAgent.CreateBuilder"/> returns one over a container of its own, and
/// <see cref="ZabbixPassiveAgentServiceCollectionExtensions.AddZabbixPassiveAgent"/> one over the application's.
/// </summary>
public interface IZabbixPassiveAgentBuilder : IZabbixItemsBuilder, IZabbixListenerBuilder
{
    /// <summary>The agent's name: the name of its options, the key it's registered under, and its host name unless set.</summary>
    new string Name { get; }

    /// <summary>The container the agent is registered in.</summary>
    new IServiceCollection Services { get; }

    // A passive agent's options are its listener's.
    [SuppressMessage("Design", "CA1033:Interface methods should be callable by child types",
        Justification = "The member is internal: the shared verbs call it to reach this builder's options, and a type that implements the interface has no use for it.")]
    void IZabbixListenerBuilder.ConfigureListener(Action<ZabbixListenerOptions> configure)
        => Services.Configure<ZabbixPassiveAgentOptions>(Name, configure);
}
