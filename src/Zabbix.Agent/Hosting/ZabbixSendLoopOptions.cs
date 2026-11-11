// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Community.Zabbix.Hosting;

/// <summary>
/// What one run of a send loop is given beside its source and its destination: the batching and retry it follows, the
/// clock it runs on, where it logs, and who is told what became of each batch.
/// </summary>
public sealed record ZabbixSendLoopOptions
{
    /// <summary>Batching, cadence, retry and the drain window.</summary>
    public ZabbixSendOptions Send { get; init; } = new();

    /// <summary>The clock the flush cadence, the backoff and the drain window run on.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Where the loop logs its failed deliveries and abandoned values; silent by default.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <summary>Told what became of every batch and every attempt, when set.</summary>
    public IProgress<ZabbixSendProgress>? Progress { get; init; }
}
