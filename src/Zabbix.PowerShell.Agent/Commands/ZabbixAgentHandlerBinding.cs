// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>One key/scriptblock binding built by <c>New-ZbxAgentHandler</c>, for piping into the <c>Register-Zbx*AgentHandler</c> commands.</summary>
/// <param name="Key">The item key's name (no wildcards: <see cref="ZabbixItemDictionary"/> keys by exact name).</param>
/// <param name="ScriptBlock">Receives the dispatched <see cref="ZabbixItemKey"/> as its first argument; its last output value becomes the answer.</param>
public sealed record ZabbixAgentHandlerBinding(string Key, ScriptBlock ScriptBlock);
