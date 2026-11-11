// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Reads the values a watch collects, as they arrive, until stopped.</para>
/// <para type="description">Each call is its own reader on the watch's own bounded buffer; a slow reader loses values to it, counted in <c>Get-ZbxWatch</c>'s <c>Dropped</c>.</para>
/// </summary>
[Cmdlet(VerbsCommunications.Receive, "ZbxWatch")]
[OutputType(typeof(ZabbixValue))]
public sealed class ReceiveZbxWatchCommand : ZabbixCmdlet
{
    /// <summary>The watch's name.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
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
        WriteAll(watch.Watcher, watch);
    }
}
