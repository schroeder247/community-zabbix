// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;

namespace Community.Zabbix.PowerShell.Diagnostics;

/// <summary>
/// One scriptblock handler invocation in flight: its root span (when anyone is listening) and its start timestamp.
/// Returned by <see cref="ZabbixPowerShellTelemetry.StartHandler"/> and closed by
/// <see cref="ZabbixPowerShellTelemetry.FinishHandler"/>.
/// </summary>
internal readonly struct ZabbixHandlerOperation : IEquatable<ZabbixHandlerOperation>
{
    internal ZabbixHandlerOperation(Activity? activity, long startTimestamp, string kind)
    {
        Activity = activity;
        StartTimestamp = startTimestamp;
        Kind = kind;
    }

    /// <summary>The span, or <see langword="null"/> when no listener sampled it.</summary>
    public Activity? Activity { get; }

    /// <summary>The <see cref="TimeProvider.GetTimestamp"/> the invocation started at.</summary>
    public long StartTimestamp { get; }

    /// <summary>The handler's kind.</summary>
    public string Kind { get; }

    /// <inheritdoc/>
    public bool Equals(ZabbixHandlerOperation other) => ReferenceEquals(Activity, other.Activity) && StartTimestamp == other.StartTimestamp;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ZabbixHandlerOperation other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Activity, StartTimestamp);

    /// <summary>Operations are equal when they are the same span started at the same instant.</summary>
    public static bool operator ==(ZabbixHandlerOperation left, ZabbixHandlerOperation right) => left.Equals(right);

    /// <summary>Operations differ when they are not the same span started at the same instant.</summary>
    public static bool operator !=(ZabbixHandlerOperation left, ZabbixHandlerOperation right) => !left.Equals(right);
}
