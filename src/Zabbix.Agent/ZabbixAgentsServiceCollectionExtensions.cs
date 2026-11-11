// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using Community.Zabbix.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Community.Zabbix;

/// <summary>
/// Binds the document's agent sections: the agents and active agents this package hosts. Why the document has one root
/// per package is written at <see cref="ZabbixConfigurationSections"/>; <c>Zabbix.Proxy</c>'s <c>AddZabbixProxies</c> binds the
/// agents too and adds what listens, and <c>Zabbix.Sdk</c>'s <c>AddZabbixClients</c> binds the servers of the same
/// document.
/// </summary>
public static class ZabbixAgentsServiceCollectionExtensions
{
    /// <summary>
    /// Registers every push agent and active agent <paramref name="document"/> declares, by name, bound to its own subsection:
    /// <see cref="ZabbixConfigurationSections.PushAgents"/> and <see cref="ZabbixConfigurationSections.ActiveAgents"/>. Each
    /// is <c>AddZabbixPushAgent(name).Bind(section)</c> or <c>AddZabbixActiveAgent(name).Bind(section)</c>, so a call of the
    /// same name adds sources, items and stores to the agent a row declares, and the row's name is the agent's host unless the
    /// row sets <c>Hostname</c>. The names are read once; what is under a name is bound, so its options monitor sees a reload.
    /// An agent sends over the wire and never calls the API, so the document's <see cref="ZabbixConfigurationSections.Servers"/>
    /// are not bound here: a host that also calls the API calls <c>Zabbix.Sdk</c>'s <c>AddZabbixClients</c> too.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="document">
    /// The <c>Zabbix</c> section, or the host's whole configuration (<c>builder.Configuration</c>): a configuration that
    /// holds none of the document's maps but a <see cref="ZabbixConfigurationSections.Section"/> is read from that section, as
    /// <c>AddZabbixClients</c> reads it.
    /// </param>
    public static IServiceCollection AddZabbixAgents(this IServiceCollection services, IConfiguration document)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(document);

        document = DocumentOf(document);
        services.AddZabbixAgentSection(document);
        foreach (IConfigurationSection agent in document.GetSection(ZabbixConfigurationSections.ActiveAgents).GetChildren())
            services.AddZabbixActiveAgent(agent.Key).Bind(agent);

        return services;
    }

    /// <summary>
    /// The document itself, when the host's whole configuration was passed for it: a configuration that holds none of the
    /// document's maps (<see cref="ZabbixConfigurationSections.Maps"/>) but a <see cref="ZabbixConfigurationSections.Section"/> is read from that
    /// section, which would otherwise register nothing and say so only when a name is first resolved. <c>Zabbix.Sdk</c>'s
    /// root applies the same rule.
    /// </summary>
    /// <param name="configuration">What the caller passed.</param>
    internal static IConfiguration DocumentOf(IConfiguration configuration)
        => !ZabbixConfigurationSections.Maps.Any(map => configuration.GetSection(map).Exists())
           && configuration.GetSection(ZabbixConfigurationSections.Section) is { } section && section.Exists()
            ? section
            : configuration;

    /// <summary>Registers every agent the document's <see cref="ZabbixConfigurationSections.PushAgents"/> section declares.</summary>
    /// <param name="services">The container.</param>
    /// <param name="document">The <c>Zabbix</c> section.</param>
    internal static IServiceCollection AddZabbixAgentSection(this IServiceCollection services, IConfiguration document)
    {
        foreach (IConfigurationSection agent in document.GetSection(ZabbixConfigurationSections.PushAgents).GetChildren())
            services.AddZabbixPushAgent(agent.Key).Bind(agent);
        return services;
    }

    /// <summary>
    /// The agents' telemetry beside every other layer's (<see cref="ZabbixWireServiceCollectionExtensions.AddZabbixWireTelemetry"/>),
    /// once per container, on the host's <see cref="IMeterFactory"/>.
    /// </summary>
    /// <param name="services">The container.</param>
    internal static IServiceCollection AddZabbixAgentTelemetry(this IServiceCollection services)
    {
        services.AddZabbixWireTelemetry();
        services.TryAddSingleton(static provider => new ZabbixAgentTelemetry(provider.GetRequiredService<IMeterFactory>(), provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}
