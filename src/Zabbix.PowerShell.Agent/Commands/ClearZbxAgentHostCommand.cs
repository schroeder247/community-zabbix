// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Removes the items and (if <c>Initialize-ZbxAgentHost</c> created it) the host a running
/// session provisioned.</para>
/// <para type="description">
/// Only ever removes what <c>Initialize-</c>/<c>Publish-ZbxAgentHost</c> recorded on the session as put on the server:
/// every tracked item always, the interface and host only when <c>Initialize-ZbxAgentHost</c> created them — a host
/// that already existed before <c>Initialize-ZbxAgentHost</c> ran is never deleted. Supports <c>-WhatIf</c> and
/// <c>-Confirm</c> (ConfirmImpact High, so it asks by default).
/// </para>
/// </summary>
/// <remarks>
/// Only ever removes what the session's own <see cref="ZabbixAgentHostRecord"/> says this bookkeeping put on the
/// server: every tracked item always, the interface and host only when this bookkeeping is the one that created
/// them — an <c>Initialize-ZbxAgentHost</c> onto an already-existing host never deletes that host.
/// </remarks>
[Cmdlet(VerbsCommon.Clear, "ZbxAgentHost", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
public sealed class ClearZbxAgentHostCommand : ZabbixAgentHostCommand
{
    /// <summary>Clears without the confirmation prompt <c>ConfirmImpact.High</c> otherwise asks for; <c>-WhatIf</c> and an explicit <c>-Confirm</c> still apply.</summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (!TryGetSession(out ZabbixAgentSession session)) return;
        if (!TryGetRecord(session, out ZabbixAgentHostRecord? record)) return;
        if (!ShouldProcessUnlessForced(record.HostName, "Clear agent host", Force)) return;

        if (TryRun(cancellationToken => ZabbixAgentHostProvisioning.ClearAsync(ZabbixClient, record, cancellationToken), Session))
            session.HostRecord = null;
    }
}
