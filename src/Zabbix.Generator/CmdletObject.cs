// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>The reviewed shape of one API object's cmdlets (<c>cmdlets.objects</c> in <c>schema/zabbix-api.json</c>).</summary>
/// <param name="Object">The API object.</param>
/// <param name="Skip">Why no cmdlet is generated for it (a hand-written command or a singleton covers it); <see langword="null"/> when they are.</param>
/// <param name="HandWrittenGet">Why its <c>Get-</c> is not generated; <see langword="null"/> when it is.</param>
/// <param name="InterfaceShorthand">The list field its <c>New-</c> also builds one element of from the interface shorthand parameters.</param>
/// <param name="ManyPerId">The id field its <c>-Id</c> selects many records by, so <c>-Limit</c> applies with it.</param>
/// <param name="Switches">Its enum fields taken as switches.</param>
/// <param name="ValueForms">Its fields whose parameters take a shell form of the value.</param>
/// <param name="Names">Its visible and technical name fields, when its requests carry both; <see langword="null"/> when its name is its only one.</param>
/// <param name="Unhidden">The properties its fields take where their own names would hide a <c>PSCmdlet</c> member.</param>
/// <param name="View">The wire fields of its read record's default table, in column order; empty when the rule over the
/// record picks them (<see cref="ZabbixTypesEmitter.TableColumns"/>).</param>
internal sealed record CmdletObject(
    string Object,
    string? Skip,
    string? HandWrittenGet,
    string? InterfaceShorthand,
    string? ManyPerId,
    EquatableArray<SwitchField> Switches,
    EquatableArray<ValueForm> ValueForms,
    NameFields? Names,
    EquatableArray<UnhiddenField> Unhidden,
    EquatableArray<string> View);
