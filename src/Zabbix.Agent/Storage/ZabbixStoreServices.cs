// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Community.Zabbix.Storage;

/// <summary>
/// Represents what a store is built with, beside its policy: its name (the tag on its telemetry, and a durable backend's table
/// or file), where it logs, the host's meter factory and the clock. An edge record: a later field is an <c>init</c>
/// property with a default, so a store's constructor never grows another parameter.
/// </summary>
public sealed record ZabbixStoreServices
{
    /// <summary>The services a store gets when nobody says otherwise: named <c>default</c>, silent, its own meter, the system clock.</summary>
    public static ZabbixStoreServices Default { get; } = new();

    /// <summary>The store's name: its telemetry tag, and a durable backend's table or file name.</summary>
    public string Name { get; init; } = "default";

    /// <summary>Where the store logs.</summary>
    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;

    /// <summary>The host's meter factory; <see langword="null"/> for a meter the store owns.</summary>
    public IMeterFactory? Meters { get; init; }

    /// <summary>The clock.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}
