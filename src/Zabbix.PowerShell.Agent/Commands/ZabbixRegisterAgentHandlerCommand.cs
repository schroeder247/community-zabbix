// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.PowerShell.Sessions;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// Shared body for <c>Register-ZbxAgentHandler</c> and its kind-checked <c>Register-ZbxActiveAgentHandler</c>/
/// <c>Register-ZbxPassiveAgentHandler</c> wrappers: registers a scriptblock on a running
/// <see cref="ZabbixAgentSession"/>, marshalled onto that session's own <see cref="ZabbixScriptRunspace"/>
/// (<see cref="ZabbixAgentSession.RegisterHandler(string, ScriptBlock)"/>) so the scriptblock never runs on the
/// session's own check-dispatch thread or on this command's pipeline thread — either of which would be unsafe.
/// </summary>
public abstract class ZabbixRegisterAgentHandlerCommand : ZabbixCmdlet
{
    private protected virtual string? Kind => null;

    /// <summary>The running agent session's name (<c>Start-ZbxActiveAgent</c>/<c>Start-ZbxPassiveAgent</c>'s <c>-Name</c>).</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNullOrWhiteSpace]
    public string Name { get; set; } = null!;

    /// <summary>The item key's name.</summary>
    [Parameter(ParameterSetName = ParameterSets.Direct, Mandatory = true, Position = 1)]
    [ValidateNotNullOrWhiteSpace]
    public string? Key { get; set; }

    /// <summary>The scriptblock; receives the dispatched key, returns the answer.</summary>
    [Parameter(ParameterSetName = ParameterSets.Direct, Mandatory = true, Position = 2)]
    public ScriptBlock? ScriptBlock { get; set; }

    /// <summary>A binding built by <c>New-ZbxAgentHandler</c>, or piped from one.</summary>
    [Parameter(ParameterSetName = ParameterSets.Binding, Mandatory = true, Position = 1, ValueFromPipeline = true)]
    public ZabbixAgentHandlerBinding? Binding { get; set; }

    /// <summary>Writes the session to the pipeline.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (!ZabbixContext.Sessions.TryGet(Name, out ZabbixAgentSession? session) || (Kind is not null && session.Kind != Kind))
        {
            WriteError(new ErrorRecord(
                new ItemNotFoundException($"no {Kind ?? "agent"} session is named '{Name}'"), "ZabbixAgentNotFound", ErrorCategory.ObjectNotFound, Name));
            return;
        }
        (string key, ScriptBlock body) = Binding is not null ? (Binding.Key, Binding.ScriptBlock) : (Key!, ScriptBlock!);
        session.RegisterHandler(key, body);
        if (PassThru) WriteObject(session);
    }

    /// <summary>The parameter sets: a key with its scriptblock, or a piped handler binding.</summary>
    private protected static class ParameterSets
    {
        /// <summary>A key and its scriptblock.</summary>
        public const string Direct = "Direct";

        /// <summary>A handler binding from <c>New-ZbxAgentHandler</c>.</summary>
        public const string Binding = "Binding";
    }
}
