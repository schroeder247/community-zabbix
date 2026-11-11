// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary><para type="synopsis">Gets the running active-proxy sessions of this runspace, or one by name.</para></summary>
[Cmdlet(VerbsCommon.Get, "ZbxActiveProxyStatus")]
[OutputType(typeof(ZabbixActiveProxySession))]
public sealed class GetZbxActiveProxyStatusCommand : ZabbixCmdlet
{
    /// <summary>The session's name; every running active-proxy session when omitted.</summary>
    [Parameter(Position = 0)]
    public string? Name { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (Name is null)
        {
            foreach (ZabbixActiveProxySession session in ZabbixContext.Sessions.OfType<ZabbixActiveProxySession>()) WriteObject(session);
        }
        else if (ZabbixContext.Sessions.TryGet(Name, out ZabbixActiveProxySession? found))
        {
            WriteObject(found);
        }
        else
        {
            WriteError(new ErrorRecord(new ItemNotFoundException($"no active-proxy session is named '{Name}'"), "ZabbixActiveProxyNotFound", ErrorCategory.ObjectNotFound, Name));
        }
    }
}
