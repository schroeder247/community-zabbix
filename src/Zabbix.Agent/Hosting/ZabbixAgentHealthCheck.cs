// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Hosting;

/// <summary>
/// Reports an agent's health to the host's health checks: the agent's own dependency, the server it delivers to,
/// never the things it monitors. In order:
/// <list type="bullet">
/// <item><b>Unhealthy</b> when one of its loops is faulting (<see cref="ZabbixAgentStatus.Fault"/>), or when values are
/// waiting (in the store or held by the send loop) and nothing has been delivered for longer than
/// <see cref="StaleAfter"/> (counted from the start when nothing ever was): the destination is down and the loss window
/// is approaching;</item>
/// <item><b>Degraded</b> when the store is past <see cref="DegradedFillFraction"/> of its cap, when the send loop holds a
/// batch the destination did not take (a failure or a paused upload), when the latest final answer was a rejection, or
/// when nothing has been delivered in the <see cref="StaleAfter"/> since the start: an agent is not healthy on faith;</item>
/// <item><b>Healthy</b> otherwise.</item>
/// </list>
/// </summary>
public sealed class ZabbixAgentHealthCheck : IHealthCheck
{
    private readonly ZabbixAgentStatus _status;
    private readonly TimeProvider _time;

    /// <summary>A check over <paramref name="status"/>.</summary>
    /// <param name="status">The agent's status.</param>
    /// <param name="time">The clock.</param>
    public ZabbixAgentHealthCheck(ZabbixAgentStatus status, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        _status = status;
        _time = time ?? TimeProvider.System;
        _ = status.Silence(_time);                                                // the status's proof is on this clock from now on
    }

    /// <summary>The store's fill, as a fraction of its cap, past which the agent is degraded.</summary>
    public double DegradedFillFraction { get; init; } = 0.8;

    /// <summary>
    /// How long values may wait undelivered before the agent is unhealthy, and the grace after start before an agent that
    /// has delivered nothing is degraded. Measured on the clock's monotonic time (<see cref="TimeProvider.GetElapsedTime(long)"/>),
    /// so a step of the wall clock neither hides a dead destination nor flags a live one.
    /// </summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        int stored = _status.Stored;
        int held = _status.Held;
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["stored"] = stored,
            ["held"] = held,
            ["delivered"] = _status.Delivered,
            ["rejected"] = _status.Rejected,
            ["abandoned"] = _status.Abandoned,
        };
        if (_status.MaxItems is { } maxItems) data["maxItems"] = maxItems;
        if (_status.StartedAt is { } started) data["startedAt"] = started;
        if (_status.LastDelivered is { } delivered) data["lastDelivered"] = delivered;
        if (_status.LastRejected is { } rejected) data["lastRejected"] = rejected;

        string name = _status.Name;
        DateTimeOffset? since = _status.LastDelivered ?? _status.StartedAt;       // the last proof that the destination works
        bool silent = _status.Silence(_time) > StaleAfter;                         // on the monotonic clock: a wall step moves nothing

        if (_status.Fault is { } fault)
            return ReportAsync(HealthCheckResult.Unhealthy($"{name}: a loop is faulting: {fault.Message}", fault, data));
        if (silent && stored + held > 0)
            return ReportAsync(HealthCheckResult.Unhealthy($"{name}: {stored + held} value(s) waiting, nothing delivered since {since:u}", data: data));
        if (_status.MaxItems is { } cap && stored >= cap * DegradedFillFraction)
            return ReportAsync(HealthCheckResult.Degraded($"{name}: store {stored}/{cap}", data: data));
        if (held > 0)
            return ReportAsync(HealthCheckResult.Degraded($"{name}: {held} value(s) held for another attempt", data: data));
        if (_status.LastRejected is { } refused && (_status.LastDelivered is not { } last || refused > last))
            return ReportAsync(HealthCheckResult.Degraded($"{name}: the destination rejected the last batch at {refused:u}", data: data));
        if (silent && _status.LastDelivered is null)
            return ReportAsync(HealthCheckResult.Degraded($"{name}: nothing delivered since the start at {since:u}", data: data));
        return ReportAsync(HealthCheckResult.Healthy($"{name}: {_status.Delivered} delivered, {stored} stored", data));
    }

    private static Task<HealthCheckResult> ReportAsync(HealthCheckResult result) => Task.FromResult(result);
}

/// <summary>
/// <see cref="ZabbixAgentHealthCheckOptions.StaleAfter"/> must be positive — not a <see cref="RangeAttribute"/> here
/// (its <c>Type, string, string</c> constructor needs a runtime <see cref="System.ComponentModel.TypeConverter"/> the
/// trimmer cannot see through, IL2026); a hand check is the trim-safe form of the same rule.
/// </summary>
internal sealed class ZabbixAgentHealthCheckStaleAfterValidator : IValidateOptions<ZabbixAgentHealthCheckOptions>
{
    public ValidateOptionsResult Validate(string? name, ZabbixAgentHealthCheckOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.StaleAfter > TimeSpan.Zero
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{nameof(ZabbixAgentHealthCheckOptions.StaleAfter)} must be positive");
    }
}
