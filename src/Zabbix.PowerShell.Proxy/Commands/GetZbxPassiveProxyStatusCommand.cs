// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary><para type="synopsis">Gets the running passive-proxy sessions of this runspace, or one by name.</para></summary>
[Cmdlet(VerbsCommon.Get, "ZbxPassiveProxyStatus")]
[OutputType(typeof(ZabbixPassiveProxySession))]
public sealed class GetZbxPassiveProxyStatusCommand : ZabbixCmdlet
{
    /// <summary>The session's name (the <c>-Name</c> it was started with); every running passive-proxy session when omitted.</summary>
    [Parameter(Position = 0)]
    public string? Name { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (Name is null)
        {
            foreach (ZabbixPassiveProxySession session in ZabbixContext.Sessions.OfType<ZabbixPassiveProxySession>()) WriteObject(session);
        }
        else if (ZabbixContext.Sessions.TryGet(Name, out ZabbixPassiveProxySession? found))
        {
            WriteObject(found);
        }
        else
        {
            WriteError(new ErrorRecord(new ItemNotFoundException($"no passive-proxy session is named '{Name}'"), "ZabbixPassiveProxyNotFound", ErrorCategory.ObjectNotFound, Name));
        }
    }
}
