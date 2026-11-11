// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell;

/// <summary>The parameter set names every generated <c>Get-</c> cmdlet shares.</summary>
internal static class ZabbixParameterSets
{
    /// <summary>No selection: every object (up to <c>-Limit</c>).</summary>
    public const string All = "All";

    /// <summary>By id.</summary>
    public const string ById = "ById";

    /// <summary>By name, case-insensitive; <c>*</c> and <c>?</c> are wildcards (<c>ZabbixNamePattern</c>).</summary>
    public const string ByName = "ByName";

    /// <summary>By the typed <c>*GetParameters</c> record: the whole <c>.get</c> grammar.</summary>
    public const string ByFilter = "ByFilter";
}
