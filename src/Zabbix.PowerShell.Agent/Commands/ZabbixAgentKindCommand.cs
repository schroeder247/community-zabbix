// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>Shared body for the two <c>Get-Zbx*AgentStatus</c> commands: both kinds are kept as the same <see cref="ZabbixAgentSession"/>, told apart by <see cref="ZabbixAgentSession.Kind"/>.</summary>
public abstract class ZabbixAgentKindCommand : ZabbixCmdlet
{
    private protected abstract string Kind { get; }

    private protected void GetByName(string? name)
    {
        if (name is null)
        {
            foreach (ZabbixAgentSession session in ZabbixContext.Sessions.OfType<ZabbixAgentSession>().Where(session => session.Kind == Kind))
                WriteObject(session);
        }
        else if (ZabbixContext.Sessions.TryGet(name, out ZabbixAgentSession? found) && found.Kind == Kind)
        {
            WriteObject(found);
        }
        else
        {
            WriteError(NotFound(name));
        }
    }

    private ErrorRecord NotFound(string? name) => new(
        new ItemNotFoundException($"no {Kind} agent session is named '{name}'"),
        "ZabbixAgentNotFound", ErrorCategory.ObjectNotFound, name);
}
