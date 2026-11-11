// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.Proxy;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Registers a scriptblock as a running active-proxy session's task handler: runs a task the
/// server hands back in an upload's acknowledgement.</para>
/// <para type="description">
/// The scriptblock runs on the session's own dedicated
/// runspace, receiving the task (a <c>ZabbixProxyTask</c>) as its argument; its last output becomes the result's
/// <c>info</c> text, reported back as a success — throwing reports a failure with the exception's message instead.
/// The result is pushed as a small follow-up page right after the tasks arrive.
/// </para>
/// </summary>
/// <remarks>The task type is <see cref="ZabbixProxyTask"/>.</remarks>
[Cmdlet(VerbsLifecycle.Register, "ZbxActiveProxyTaskHandler")]
[OutputType(typeof(ZabbixActiveProxySession))]
public sealed class RegisterZbxActiveProxyTaskHandlerCommand : ZabbixCmdlet
{
    /// <summary>The running active-proxy session's name.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNullOrWhiteSpace]
    public string Name { get; set; } = null!;

    /// <summary>The handler; receives the task, returns the text to report back as its result.</summary>
    [Parameter(Mandatory = true, Position = 1)]
    public ScriptBlock ScriptBlock { get; set; } = null!;

    /// <summary>Writes the session to the pipeline.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (!ZabbixContext.Sessions.TryGet(Name, out ZabbixActiveProxySession? session))
        {
            WriteError(new ErrorRecord(new ItemNotFoundException($"no active-proxy session is named '{Name}'"), "ZabbixActiveProxyNotFound", ErrorCategory.ObjectNotFound, Name));
            return;
        }
        session.RegisterTaskHandler(ScriptBlock);
        if (PassThru) WriteObject(session);
    }
}
