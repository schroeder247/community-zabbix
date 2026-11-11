// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Sessions;

/// <summary>
/// Provides the base of a command over one running session found by name: the <c>Get-</c>/<c>Clear-Zbx*Log</c> and
/// <c>Stop-Zbx*</c> cmdlets of the agent and proxy sessions. The derived cmdlet says which kind of session it means;
/// the lookup, the not-found error and the parameter are here.
/// </summary>
public abstract class ZabbixSessionCmdlet : ZabbixCmdlet
{
    /// <summary>The session's name (the <c>-Name</c> it was started with).</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNullOrWhiteSpace]
    public string Name { get; set; } = null!;

    /// <summary>The kind of session, as a message names it (<c>active-agent</c>, <c>passive-proxy</c>).</summary>
    private protected abstract string SessionKind { get; }

    /// <summary>The not-found error's id (<c>ZabbixAgentNotFound</c>, <c>ZabbixActiveProxyNotFound</c>).</summary>
    private protected abstract string NotFoundErrorId { get; }

    /// <summary>The running session of this command's kind named <paramref name="name"/>, or <see langword="null"/>.</summary>
    /// <param name="name">The name.</param>
    private protected abstract IZabbixSession? Find(string name);

    /// <summary>
    /// The session named <see cref="Name"/>, which the runspace's context owns (this command never disposes it but by
    /// stopping it); a non-terminating <c>ObjectNotFound</c> error and <see langword="null"/> when there is none.
    /// </summary>
    private protected IZabbixSession? Found()
    {
        IZabbixSession? session = Find(Name);
        if (session is null)
            WriteError(new ErrorRecord(new ItemNotFoundException($"no {SessionKind} session is named '{Name}'"), NotFoundErrorId, ErrorCategory.ObjectNotFound, Name));
        return session;
    }
}
