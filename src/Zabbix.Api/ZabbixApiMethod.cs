// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// One API method as the Zabbix source states it (generated into <see cref="ZabbixApiMethods.All"/>).
/// </summary>
/// <param name="Name">The method, as Zabbix routes it (lower case; the server lower-cases what it is sent).</param>
/// <param name="Since">The first version with the method.</param>
/// <param name="RemovedIn">The first version without it, or null.</param>
/// <param name="MinimumUserType">The least user type <c>ACCESS_RULES</c> admits, or null when anyone may call it.</param>
/// <param name="IsRead">Nothing reachable from the method writes (no <c>DB::insert</c>/<c>update</c>/<c>delete</c>, audit or server call).</param>
/// <param name="IsIdempotent">Sending it twice leaves the state sending it once does: every read, and the writes the API description marks safe to repeat.</param>
/// <param name="RequiresAuthentication">Its <c>requiresAuthentication()</c>: false for the methods called before a session exists.</param>
/// <param name="AcceptsAuthenticationSince">The version from which the server also accepts an <c>Authorization</c> header on an anonymous method.</param>
/// <param name="Replacement">What to use where the method is gone.</param>
public sealed record ZabbixApiMethod(
    string Name,
    Version Since,
    Version? RemovedIn,
    ZabbixRoleType? MinimumUserType,
    bool IsRead,
    bool IsIdempotent,
    bool RequiresAuthentication,
    Version? AcceptsAuthenticationSince,
    string? Replacement);
