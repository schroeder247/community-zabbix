// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>What every agent-host cmdlet shares: resolving the running session (when one is named) and its handler keys.</summary>
public abstract class ZabbixAgentHostCommand : ZabbixServerCmdlet
{
    /// <summary>
    /// The running agent session (<c>Start-ZbxActiveAgent</c>/<c>Start-ZbxPassiveAgent</c>'s <c>-Name</c>) whose
    /// handler keys become items, and which remembers what was created on the server.
    /// </summary>
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrWhiteSpace]
    public string Session { get; set; } = null!;

    private protected bool TryGetSession(out ZabbixAgentSession session)
    {
        if (ZabbixContext.Sessions.TryGet(Session, out ZabbixAgentSession? found))
        {
            session = found;
            return true;
        }
        WriteError(new ErrorRecord(new ItemNotFoundException($"no agent session is named '{Session}'"), "ZabbixAgentNotFound", ErrorCategory.ObjectNotFound, Session));
        session = null!;
        return false;
    }

    private protected bool TryGetRecord(ZabbixAgentSession session, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ZabbixAgentHostRecord? record)
    {
        record = session.HostRecord;
        if (record is not null) return true;
        WriteError(new ErrorRecord(
            new InvalidOperationException($"no agent host has been initialised for session '{Session}': run Initialize-ZbxAgentHost or Publish-ZbxAgentHost first"),
            "ZabbixAgentHostNotInitialized", ErrorCategory.InvalidOperation, Session));
        return false;
    }
}
