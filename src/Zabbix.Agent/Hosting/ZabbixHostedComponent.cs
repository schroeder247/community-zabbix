// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Hosting;

namespace Community.Zabbix.Hosting;

/// <summary>
/// Runs a <see cref="ZabbixComponent"/> in an application's host, phase by phase, so a registration hosts the same class an
/// application constructs. The container owns the component and disposes it; this holds nothing of its own.
/// </summary>
/// <param name="component">The agent or proxy.</param>
internal sealed class ZabbixHostedComponent(ZabbixComponent component) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => component.HostStartingAsync(cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) => component.HostStartAsync(cancellationToken);

    public Task StartedAsync(CancellationToken cancellationToken) => component.HostStartedAsync(cancellationToken);

    public Task StoppingAsync(CancellationToken cancellationToken) => component.HostStoppingAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => component.HostStopAsync(cancellationToken);

    public Task StoppedAsync(CancellationToken cancellationToken) => component.HostStoppedAsync(cancellationToken);
}
