// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>A get option every <c>Get-</c> whose parameters carry it takes as a switch (<c>editable</c>: <c>-Editable</c>).</summary>
/// <param name="Wire">The option's wire name; the option is an API flag.</param>
/// <param name="Parameter">The switch's name.</param>
/// <param name="Summary">The switch's help sentence.</param>
internal sealed record GetSwitch(string Wire, string Parameter, string Summary);
