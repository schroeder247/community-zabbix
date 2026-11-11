// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.PowerShell.Sessions;
using Community.Zabbix.Proxy;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Registers a scriptblock as a running passive-proxy session's configuration handler: observes,
/// and may veto, every configuration the server pushes.</para>
/// <para type="description">
/// The scriptblock runs on the session's own dedicated
/// runspace (as <c>Register-ZbxAgentHandler</c> scriptblocks do), with the incoming and currently-held configuration
/// (<c>ZabbixProxyConfig</c>) as its two arguments. Returning nothing (or <c>$null</c>) accepts
/// the configuration; returning a non-empty string, or throwing, vetoes it — the string (or the exception's message)
/// becomes the <c>"failed"</c> reply's <c>info</c>, and the proxy keeps the configuration it already held.
/// </para>
/// </summary>
/// <remarks>
/// The runspace is <see cref="ZabbixScriptRunspace"/>, the same bridge <c>Register-ZbxAgentHandler</c> uses; the
/// configurations are <see cref="ZabbixProxyConfig"/>.
/// </remarks>
[Cmdlet(VerbsLifecycle.Register, "ZbxPassiveProxyConfigHandler")]
[OutputType(typeof(ZabbixPassiveProxySession))]
public sealed class RegisterZbxPassiveProxyConfigHandlerCommand : ZabbixCmdlet
{
    /// <summary>The running passive-proxy session's name.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNullOrWhiteSpace]
    public string Name { get; set; } = null!;

    /// <summary>The handler; receives <c>(incoming, held)</c>, returns nothing to accept or a veto reason to refuse.</summary>
    [Parameter(Mandatory = true, Position = 1)]
    public ScriptBlock ScriptBlock { get; set; } = null!;

    /// <summary>Writes the session to the pipeline.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (!ZabbixContext.Sessions.TryGet(Name, out ZabbixPassiveProxySession? session))
        {
            WriteError(new ErrorRecord(new ItemNotFoundException($"no passive-proxy session is named '{Name}'"), "ZabbixPassiveProxyNotFound", ErrorCategory.ObjectNotFound, Name));
            return;
        }
        session.RegisterConfigHandler(ScriptBlock);
        if (PassThru) WriteObject(session);
    }
}
