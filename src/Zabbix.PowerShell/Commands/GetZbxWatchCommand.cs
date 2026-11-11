// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets the running watches of this session.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ZbxWatch")]
[OutputType(typeof(ZabbixWatch))]
public sealed class GetZbxWatchCommand : ZabbixCmdlet
{
    /// <summary>The watch's name; every running watch when omitted.</summary>
    [Parameter(Position = 0)]
    public string? Name { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (Name is null)
        {
            foreach (ZabbixWatch watch in ZabbixContext.Sessions.OfType<ZabbixWatch>()) WriteObject(watch);
        }
        else if (ZabbixContext.Sessions.TryGet(Name, out ZabbixWatch? found))
        {
            WriteObject(found);
        }
        else
        {
            WriteError(new ErrorRecord(new ItemNotFoundException($"no watch is named '{Name}'"), "ZabbixWatchNotFound", ErrorCategory.ObjectNotFound, Name));
        }
    }
}
