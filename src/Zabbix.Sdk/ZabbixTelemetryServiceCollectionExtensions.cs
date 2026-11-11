// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using Community.Zabbix.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Community.Zabbix;

/// <summary>
/// Registers the API's and the pull plane's telemetry once per container, on the host's <see cref="IMeterFactory"/>.
/// <c>AddZabbix</c>, the agents (<c>Zabbix.Agent</c>, which adds the wire's and the agents' own layers) and the
/// proxies call it, so a host that registers any of them gets one instance per layer, its instruments on the
/// host's meters (and so on whatever the host exports), and the hosted components resolve them rather than build their own.
/// </summary>
internal static class ZabbixTelemetryServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ZabbixApiTelemetry"/> and <see cref="ZabbixTreeTelemetry"/> (each only once).</summary>
    /// <param name="services">The container.</param>
    public static IServiceCollection AddZabbixTelemetry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMetrics();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ZabbixApiTelemetry>();
        services.TryAddSingleton(static provider => new ZabbixTreeTelemetry(provider.GetRequiredService<IMeterFactory>(), provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}
