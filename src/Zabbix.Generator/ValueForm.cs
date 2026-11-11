// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>The attributes a field's cmdlet parameter carries to take the field's value in a form the shell writes (<c>[ZabbixMapColor]</c>: <c>DarkGray</c> for <c>333333</c>).</summary>
/// <param name="Wire">The field's wire name.</param>
/// <param name="Attributes">The attribute names, in order.</param>
internal sealed record ValueForm(string Wire, EquatableArray<string> Attributes);
