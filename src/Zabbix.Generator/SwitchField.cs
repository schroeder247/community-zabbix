// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>A two-member enum field its cmdlet parameter takes as a switch: <see cref="On"/> when present, <see cref="Off"/> as <c>-X:$false</c>.</summary>
/// <param name="Wire">The field's wire name.</param>
/// <param name="On">The member sent when the switch is present.</param>
/// <param name="Off">The member sent when the switch is given as false.</param>
internal sealed record SwitchField(string Wire, string On, string Off);
