// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Community.Zabbix.Listening;

/// <summary>
/// The health of a component that answers only while it listens, a passive agent or a passive proxy: healthy while its
/// listener accepts, degraded before it starts, and unhealthy when it stopped listening.
/// </summary>
/// <param name="name">The component's name.</param>
/// <param name="role">What it is, for the message: <c>agent</c> or <c>proxy</c>.</param>
/// <param name="listener">Reads its listener when the check runs; <see langword="null"/> before it starts.</param>
internal sealed class ZabbixListenerHealthCheck(string name, string role, Func<ZabbixListener?> listener) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(listener() switch
        {
            null => HealthCheckResult.Degraded($"{name}: the {role} has not started"),
            { IsAccepting: true } listening => HealthCheckResult.Healthy($"{name}: listening on {listening.LocalEndPoint}, {listening.InFlight} connection(s) in flight"),
            _ => HealthCheckResult.Unhealthy($"{name}: the {role} isn't listening"),
        });
}
