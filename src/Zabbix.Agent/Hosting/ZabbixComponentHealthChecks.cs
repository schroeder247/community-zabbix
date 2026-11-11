// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Hosting;

/// <summary>
/// Puts an agent or a proxy on its container's health checks, under the <c>zabbix</c> tag and its role's, <c>agent</c> or
/// <c>proxy</c>: the one registration the builders' <c>AddHealthCheck</c> verbs make.
/// </summary>
internal static class ZabbixComponentHealthChecks
{
    /// <summary>The tag of an agent's check.</summary>
    public const string Agent = "agent";

    /// <summary>The tag of a proxy's check.</summary>
    public const string Proxy = "proxy";

    /// <summary>
    /// A check over a push or active agent's statuses, or an active proxy's: as healthy as its worst destination. The
    /// statuses are read when the check runs, so destinations built at start are seen.
    /// </summary>
    /// <param name="services">The component's container.</param>
    /// <param name="name">The check's name.</param>
    /// <param name="statuses">Reads the component's statuses from the container.</param>
    /// <param name="configure">Sets the thresholds.</param>
    /// <param name="role">The role's tag: <see cref="Agent"/> or <see cref="Proxy"/>.</param>
    /// <exception cref="OptionsValidationException">The thresholds don't validate.</exception>
    public static void Add(IServiceCollection services, string name, Func<IServiceProvider, IReadOnlyList<ZabbixComponentStatus>> statuses, Action<ZabbixComponentHealthCheckOptions> configure,
        string role = Agent)
    {
        var options = new ZabbixComponentHealthCheckOptions();
        configure(options);
        List<string> failures = [.. new IValidateOptions<ZabbixComponentHealthCheckOptions>[] { new ZabbixComponentHealthCheckOptionsValidator(), new ZabbixComponentHealthCheckStaleAfterValidator() }
            .Select(validator => validator.Validate(name, options)).Where(result => result.Failed).SelectMany(result => result.Failures ?? [])];
        if (failures.Count > 0) throw new OptionsValidationException(name, typeof(ZabbixComponentHealthCheckOptions), failures);
        Add(services, name, provider => new Destinations(() => statuses(provider), provider.GetService<TimeProvider>(), options, role), role);
    }

    /// <summary>A check of the caller's own, under the <c>zabbix</c> tag and its role's.</summary>
    /// <param name="services">The component's container.</param>
    /// <param name="name">The check's name.</param>
    /// <param name="check">Makes the check from the container.</param>
    /// <param name="role">The role's tag: <see cref="Agent"/> or <see cref="Proxy"/>.</param>
    public static void Add(IServiceCollection services, string name, Func<IServiceProvider, IHealthCheck> check, string role = Agent)
        => services.AddHealthChecks().Add(new HealthCheckRegistration(name, check, failureStatus: null, tags: ["zabbix", role]));

    private sealed class Destinations(Func<IReadOnlyList<ZabbixComponentStatus>> statuses, TimeProvider? time, ZabbixComponentHealthCheckOptions options, string role) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            HealthCheckResult? worst = null;
            foreach (ZabbixComponentStatus status in statuses())
            {
                HealthCheckResult result = await new ZabbixComponentHealthCheck(status, time) { DegradedFillFraction = options.DegradedFillFraction, StaleAfter = options.StaleAfter }
                    .CheckHealthAsync(context, cancellationToken).ConfigureAwait(false);
                if (worst is not { } held || result.Status < held.Status) worst = result;
            }
            return worst ?? HealthCheckResult.Degraded($"the {role} has not started");
        }
    }
}
