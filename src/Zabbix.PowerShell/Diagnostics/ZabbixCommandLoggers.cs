// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell.Diagnostics;

/// <summary>
/// The loggers of one command that talks to a peer with no connection (<c>Send-ZbxData -Server</c>,
/// <c>Get-ZbxAgentItem</c>): every level goes to the running command's own streams, through the provider a connection's
/// and a session's loggers use (<see cref="ZabbixPowerShellLoggerProvider"/>), composed as every logger factory of the
/// module is (<see cref="ZabbixModuleTelemetry.CreateLoggers"/>). Disposed when the command ends.
/// </summary>
internal sealed class ZabbixCommandLoggers : IDisposable
{
    private readonly ZabbixPowerShellLoggerProvider _provider;

    /// <summary>Loggers for one command.</summary>
    /// <param name="time">The clock a record no command takes is buffered on.</param>
    public ZabbixCommandLoggers(TimeProvider time)
    {
        _provider = new ZabbixPowerShellLoggerProvider(time);
        Factory = ZabbixModuleTelemetry.CreateLoggers(_provider);
    }

    /// <summary>The factory the command's wire objects take their loggers from.</summary>
    public ILoggerFactory Factory { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        Factory.Dispose();
        _provider.Dispose();
    }
}
