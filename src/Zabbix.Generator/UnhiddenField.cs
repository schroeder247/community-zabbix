// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>
/// The property a field's parameter takes when its own name would hide a member of <c>PSCmdlet</c> (<c>Host</c>); the
/// parameter keeps its own spelling as an alias.
/// </summary>
/// <param name="Wire">The field's wire name.</param>
/// <param name="Property">The property's name.</param>
internal sealed record UnhiddenField(string Wire, string Property);
