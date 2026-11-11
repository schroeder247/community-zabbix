// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Builds a key/scriptblock binding for piping into <c>Register-ZbxAgentHandler</c> (or its
/// kind-checked wrappers), so a catalogue of handlers can be built once and registered onto several sessions.</para>
/// </summary>
[Cmdlet(VerbsCommon.New, "ZbxAgentHandler")]
[OutputType(typeof(ZabbixAgentHandlerBinding))]
public sealed class NewZbxAgentHandlerCommand : ZabbixCmdlet
{
    /// <summary>The item key's name.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrWhiteSpace]
    public string Key { get; set; } = null!;

    /// <summary>The scriptblock; receives the dispatched key, returns the answer.</summary>
    [Parameter(Mandatory = true, Position = 1)]
    public ScriptBlock ScriptBlock { get; set; } = null!;

    /// <inheritdoc/>
    protected override void ProcessRecord() => WriteObject(new ZabbixAgentHandlerBinding(Key, ScriptBlock));
}
