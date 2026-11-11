// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>
/// A get option every <c>Get-</c> whose parameters carry it takes as an array of its enum (<c>severities</c>:
/// <c>-Severity High, Disaster</c>), rather than only through <c>-Filter</c>.
/// </summary>
/// <param name="Wire">The option's wire name; the option is a list of enum values.</param>
/// <param name="Parameter">The parameter's name.</param>
/// <param name="Summary">The parameter's help sentence.</param>
internal sealed record GetList(string Wire, string Parameter, string Summary);
