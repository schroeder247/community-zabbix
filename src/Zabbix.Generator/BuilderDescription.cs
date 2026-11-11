// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>A nested-record builder, <c>New-Zbx&lt;Concept&gt;</c> (<c>cmdlets.builders</c> in <c>schema/zabbix-api.json</c>).</summary>
/// <param name="Concept">The name a user writes (<c>Tag</c>, <c>MapShape</c>).</param>
/// <param name="Record">The nested request record it builds.</param>
/// <param name="Positional">The wire fields it takes by position, in order.</param>
/// <param name="Noun">What it builds, for the help (<c>map shape</c>).</param>
/// <param name="Use">The owner parameters that take it, for the help.</param>
/// <param name="What">What the part does in Zabbix, for the help.</param>
/// <param name="ValueForms">Its fields whose parameters take a shell form of the value.</param>
/// <param name="OwnerSupplied">Its required fields the owner's command fills when the builder leaves them out, with the help's sentence.</param>
internal sealed record BuilderDescription(
    string Concept,
    string Record,
    EquatableArray<string> Positional,
    string Noun,
    string Use,
    string What,
    EquatableArray<ValueForm> ValueForms,
    EquatableArray<WireNote> OwnerSupplied);
