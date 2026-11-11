// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>A sentence about one field, keyed by its wire name: the condition a field is required under, or what fills it when it is left out.</summary>
/// <param name="Wire">The field's wire name.</param>
/// <param name="Text">The sentence, as the description gives it.</param>
internal sealed record WireNote(string Wire, string Text);
