// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;

namespace Community.Zabbix.Diagnostics;

/// <summary>
/// One operation in flight: its span (when anyone is listening) and its start timestamp. It is returned by
/// <see cref="ZabbixLayerTelemetry.Start"/> and closed by <see cref="ZabbixLayerTelemetry.Finish"/>, which writes the span
/// and the duration sample together so the two can never disagree.
/// </summary>
internal readonly struct ZabbixOperation : IEquatable<ZabbixOperation>
{
    internal ZabbixOperation(Activity? activity, long startTimestamp, TagList tags)
    {
        Activity = activity;
        StartTimestamp = startTimestamp;
        Tags = tags;
    }

    /// <summary>The span, or <see langword="null"/> when no listener sampled it (then it costs nothing).</summary>
    public Activity? Activity { get; }

    /// <summary>The <see cref="TimeProvider.GetTimestamp"/> the operation started at.</summary>
    public long StartTimestamp { get; }

    /// <summary>The tags shared by the span and the duration sample.</summary>
    public TagList Tags { get; }

    /// <inheritdoc/>
    public bool Equals(ZabbixOperation other) => ReferenceEquals(Activity, other.Activity) && StartTimestamp == other.StartTimestamp;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ZabbixOperation other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Activity, StartTimestamp);

    /// <summary>Operations are equal when they are the same span started at the same instant.</summary>
    public static bool operator ==(ZabbixOperation left, ZabbixOperation right) => left.Equals(right);

    /// <summary>Operations differ when they are not the same span started at the same instant.</summary>
    public static bool operator !=(ZabbixOperation left, ZabbixOperation right) => !left.Equals(right);
}
