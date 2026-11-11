// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using Community.Zabbix.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix;

/// <summary>
/// Represents a proxy being set up, in the shape of <c>IHttpClientBuilder</c>: its name and the container it's registered
/// in. <see cref="ZabbixProxyBuilderExtensions"/> holds the verbs both proxies have, and the store's verbs
/// (<see cref="ZabbixStoreBuilderExtensions"/>) and the listener's (<see cref="ZabbixListenerBuilderExtensions"/>) are the
/// ones a push agent and a passive agent have. <see cref="IZabbixActiveProxyBuilder"/> and
/// <see cref="IZabbixPassiveProxyBuilder"/> are each proxy's own.
/// </summary>
public interface IZabbixProxyBuilder : IZabbixStoreBuilder, IZabbixListenerBuilder
{
    /// <summary>The proxy's name as the server knows it: the name of its options, and the key it's registered under.</summary>
    string Name { get; }

    /// <summary>The container the proxy is registered in.</summary>
    IServiceCollection Services { get; }

    // A proxy keeps its history store in its named options, built at start.
    [SuppressMessage("Design", "CA1033:Interface methods should be callable by child types",
        Justification = "The member is internal: the shared verbs call it to reach this builder's options, and a type that implements the interface has no use for it.")]
    void IZabbixStoreBuilder.SetStore(Func<IServiceProvider, ZabbixStorePolicy<ZabbixValue>, ZabbixStoreServices, ZabbixStore<ZabbixValue>>? create)
        => Services.Configure<ZabbixProxyOptions>(Name, options => options.Storage = create);

    // A proxy's listener is its trapper.
    [SuppressMessage("Design", "CA1033:Interface methods should be callable by child types",
        Justification = "The member is internal: the shared verbs call it to reach this builder's options, and a type that implements the interface has no use for it.")]
    void IZabbixListenerBuilder.ConfigureListener(Action<ZabbixListenerOptions> configure)
        => Services.Configure<ZabbixProxyOptions>(Name, options => configure(options.Trapper));
}
