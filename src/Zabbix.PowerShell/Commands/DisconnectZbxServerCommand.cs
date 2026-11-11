// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Closes a connection and removes it from the session.</para>
/// <para type="description">Closes the named connection (the current one by default); a call still in flight on it
/// finishes first. A connection that logged in with <c>-Credential</c> is logged out (<c>user.logout</c>) before the
/// command returns, so its session does not wait on the server to expire; Ctrl+C stops the wait, not the logout. A
/// logout that fails is logged and the session is left to expire.</para>
/// </summary>
[Cmdlet(VerbsCommunications.Disconnect, "ZbxServer")]
public sealed class DisconnectZbxServerCommand : ZabbixCmdlet
{
    /// <summary>The connection's name; the current server when omitted.</summary>
    [Parameter(Position = 0, ValueFromPipelineByPropertyName = true)]
    [ZabbixConnectionCompleter]
    public string? Name { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        string? name = Name ?? ZabbixContext.Current?.Name;
        if (name is null || !ZabbixContext.TryRemove(name, out ZabbixConnection? connection))
        {
            WriteError(new ErrorRecord(new ItemNotFoundException($"no connection is named '{name}'"), "ZabbixConnectionNotFound", ErrorCategory.ObjectNotFound, name));
            return;
        }
        // The logout, when this was the last lease out; a call still in flight logs out when it returns.
        Task loggedOut = connection.LoggedOut;
        TryRun(cancellationToken => loggedOut.WaitAsync(cancellationToken), name);
    }
}
