// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix;

/// <summary>
/// Binds the wider document: the agents, plus what only a consumer hosting a listener can build. <c>Zabbix.Proxy</c> carries Kestrel's connection layer and, with it, passive
/// agents, proxies (either mode) and the trapper — <c>Zabbix.Sdk</c> and <c>Zabbix.Agent</c> cannot reference this project without a cycle
/// (see <see cref="ZabbixConfigurationSections"/>'s remarks), so the sections that need it are bound here instead, in the same
/// document, under the same <see cref="ZabbixConfigurationSections.Section"/>.
/// </summary>
public static class ZabbixProxiesServiceCollectionExtensions
{
    /// <summary>
    /// Registers every agent, agent-nested trapper, passive agent and proxy <paramref name="document"/> declares, by name,
    /// bound to its own subsection. A proxy row is wired to
    /// <see cref="ZabbixActiveProxyServiceCollectionExtensions.AddZabbixActiveProxy"/> or
    /// <see cref="ZabbixPassiveProxyServiceCollectionExtensions.AddZabbixPassiveProxy"/> by its own
    /// <see cref="ZabbixProxyOptions.Mode"/> (Zabbix's <c>ProxyMode</c>) — one row serves both verbs, so a document
    /// never says which verb, only which mode, exactly as the proxy's own configuration file would.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="document">
    /// The <c>Zabbix</c> section, or the host's whole configuration (<c>builder.Configuration</c>): a configuration that
    /// holds none of the document's maps but a <see cref="ZabbixConfigurationSections.Section"/> is read from that section, as the
    /// other roots read it.
    /// </param>
    /// <remarks>
    /// The active agents' section is bound by <see cref="ZabbixAgentsServiceCollectionExtensions.AddZabbixAgents"/> only: a
    /// host of both calls both roots.
    /// </remarks>
    public static IServiceCollection AddZabbixProxies(this IServiceCollection services, IConfiguration document)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(document);

        document = ZabbixAgentsServiceCollectionExtensions.DocumentOf(document);
        services.AddZabbixAgentSection(document);

        foreach (IConfigurationSection agent in document.GetSection(ZabbixConfigurationSections.PushAgents).GetChildren())
        {
            IConfigurationSection trapper = agent.GetSection(ZabbixConfigurationSections.Trapper);
            if (trapper.Exists()) services.AddZabbixPushAgent(agent.Key).AddTrapper(trapper);
        }

        foreach (IConfigurationSection agent in document.GetSection(ZabbixConfigurationSections.PassiveAgents).GetChildren())
            services.AddZabbixPassiveAgent(agent.Key).Bind(agent);

        foreach (IConfigurationSection proxy in document.GetSection(ZabbixConfigurationSections.Proxies).GetChildren())
        {
            ZabbixProxyRole mode = proxy.GetValue<ZabbixProxyRole?>(nameof(ZabbixProxyOptions.Mode)) ?? ZabbixProxyRole.Active;
            if (mode == ZabbixProxyRole.Active) services.AddZabbixActiveProxy(proxy.Key).Bind(proxy);
            else services.AddZabbixPassiveProxy(proxy.Key).Bind(proxy);
        }

        return services;
    }
}
