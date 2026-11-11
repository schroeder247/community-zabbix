// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Community.Zabbix;

/// <summary>
/// Registers the framed wire's telemetry once per container, on the host's <see cref="IMeterFactory"/>.
/// <c>Zabbix.Wire</c> has no container of its own (a sender or a getter built directly reports to
/// one process-wide instance), so the hosts that resolve the wire from a container register it here.
/// </summary>
internal static class ZabbixWireServiceCollectionExtensions
{
    /// <summary>Registers the host's meters, its clock and the wire's telemetry (each only once).</summary>
    /// <param name="services">The container.</param>
    public static IServiceCollection AddZabbixWireTelemetry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMetrics();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(static provider => new ZabbixWireTelemetry(provider.GetRequiredService<IMeterFactory>(), provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}
