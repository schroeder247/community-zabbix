// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix.Hosting;

/// <summary>The registration every agent and proxy shares: the component a container builds, and hands out, by its name.</summary>
internal static class ZabbixComponentRegistration
{
    /// <summary>
    /// Registers the component named <paramref name="name"/> as the container's: the container holds it through a
    /// <see cref="ZabbixComponentOwner{TComponent}"/>, which releases it when the container is disposed, and hands out the
    /// component itself, shared, so a caller's dispose leaves it running.
    /// </summary>
    /// <typeparam name="TComponent">The agent or proxy.</typeparam>
    /// <param name="services">The container.</param>
    /// <param name="name">The component's name, the key it's registered under.</param>
    /// <param name="create">Builds the component from the container and the key.</param>
    public static void AddShared<TComponent>(IServiceCollection services, string name, Func<IServiceProvider, object?, TComponent> create)
        where TComponent : ZabbixComponent
    {
        services.AddKeyedSingleton(name, (provider, key) => new ZabbixComponentOwner<TComponent>(create(provider, key)));
        services.AddKeyedSingleton(name, static (provider, key) => provider.GetRequiredKeyedService<ZabbixComponentOwner<TComponent>>(key).Component);
    }
}
