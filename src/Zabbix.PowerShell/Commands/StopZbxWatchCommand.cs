// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Management.Automation;
using Community.Zabbix.PowerShell.Diagnostics;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Stops a watch.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Stop, "ZbxWatch", SupportsShouldProcess = true)]
public sealed class StopZbxWatchCommand : ZabbixCmdlet
{
    /// <summary>The watch's name.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNullOrWhiteSpace]
    public string? Name { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (!ZabbixContext.Sessions.TryGet(Name!, out ZabbixWatch? watch))
        {
            WriteError(new ErrorRecord(new ItemNotFoundException($"no watch is named '{Name}'"), "ZabbixWatchNotFound", ErrorCategory.ObjectNotFound, Name));
            return;
        }
        if (!ShouldProcess(Name, "Stop watch")) return;
        ZabbixContext.Sessions.Remove(watch);
        using Activity? span = ZabbixPowerShellTelemetry.StartCommandSpan(MyInvocation.MyCommand.Name);
        if (TryRun(async cancellationToken =>
            {
                await watch.DisposeAsync().ConfigureAwait(false);
                return true;
            }, out _, Name))
        {
        }
    }
}
