// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Hosting;

/// <summary>The thresholds of an agent's health check.</summary>
public sealed class ZabbixAgentHealthCheckOptions
{
    /// <inheritdoc cref="ZabbixAgentHealthCheck.DegradedFillFraction"/>
    [Range(0.0, 1.0)]
    public double DegradedFillFraction { get; set; } = 0.8;

    /// <inheritdoc cref="ZabbixAgentHealthCheck.StaleAfter"/>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>The generated half of <see cref="ZabbixAgentHealthCheckOptions"/>'s validation (<see cref="ZabbixAgentHealthCheckOptions.DegradedFillFraction"/>'s <see cref="RangeAttribute"/>); run where <c>AddHealthCheck</c> builds the options (they are never a DI <c>IOptions&lt;T&gt;</c>: one host check per call, configured once).</summary>
[OptionsValidator]
internal sealed partial class ZabbixAgentHealthCheckOptionsValidator : IValidateOptions<ZabbixAgentHealthCheckOptions>
{
}
