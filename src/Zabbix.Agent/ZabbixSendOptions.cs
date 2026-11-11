// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel.DataAnnotations;
using System.Threading.Channels;
using Community.Zabbix.Hosting;

namespace Community.Zabbix;

/// <summary>How a stream of values becomes frames on the wire: batch size, flush cadence, retry backoff, and the loss window at stop.</summary>
public sealed class ZabbixSendOptions : IValidatableObject
{
    /// <summary>How many values one frame carries at most.</summary>
    [Range(1, 100_000)]
    public int BatchSize { get; set; } = 250;

    /// <summary>How long a partial batch waits before it is sent anyway.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The first backoff after a delivery that asked to be retried; doubled with jitter up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest backoff between retries.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a stopped loop keeps delivering what it has taken and what its source still has ready: the loss window at
    /// shutdown, counted from the stop. What is not delivered by then goes back to a store (a durable store keeps it for
    /// the next process), is handed back to a source that can take it (a <see cref="ChannelWriter{T}"/> that is not
    /// completed), and is lost otherwise. The default, 10 seconds, leaves an agent host's two windows (its sources, then
    /// its store) inside the generic host's default 30-second <c>HostOptions.ShutdownTimeout</c>.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(10);

    IEnumerable<ValidationResult> IValidatableObject.Validate(ValidationContext validationContext)
        => ZabbixOptionsRanges.Failures(
            ZabbixOptionsRanges.Duration(FlushInterval, nameof(FlushInterval)),
            ZabbixOptionsRanges.Duration(RetryDelay, nameof(RetryDelay)),
            ZabbixOptionsRanges.Duration(MaxRetryDelay, nameof(MaxRetryDelay)),
            ZabbixOptionsRanges.Duration(DrainTimeout, nameof(DrainTimeout), zeroAllowed: true));
}
