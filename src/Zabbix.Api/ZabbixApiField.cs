// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// One field of a method's request that a version of the server refuses (generated into the field table behind
/// <see cref="ZabbixApiMethods.TryGetField"/>): the version whose validator no longer lists it and refuses what it does
/// not list (no <c>API_ALLOW_UNEXPECTED</c>), and the field that took its place, as the source names it.
/// </summary>
/// <param name="Method">The API method, as Zabbix routes it.</param>
/// <param name="Field">The wire field.</param>
/// <param name="RemovedIn">The first version that refuses it.</param>
/// <param name="Replacement">The field to send instead: the rule's own <c>'replacement'</c>, or the field the older service converted it into (<c>hostids</c> → <c>hosts</c>, <c>CMaintenance.php</c> 7.0:329).</param>
public sealed record ZabbixApiField(string Method, string Field, Version RemovedIn, string? Replacement);
