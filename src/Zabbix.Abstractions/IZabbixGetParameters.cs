// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents the parameters every <c>.get</c> accepts, whatever the object: eight of the eleven parameters every
/// queryable <c>.get</c> parameter record shares. The other three, <c>output</c>, <c>sortfield</c> and <c>sortorder</c>,
/// name the fields of the object's record, so they're on <see cref="IZabbixGetParameters{TRecord}"/>, which the generated
/// <c>Zabbix*GetParameters</c> records implement. This interface is what lets one generic reader (the PowerShell
/// module's <c>Get-</c> cmdlets) fill the common options of any of them.
/// </summary>
public interface IZabbixGetParameters
{
    /// <summary><c>filter</c>: exact matches, any of a list.</summary>
    ZabbixGetConditions Filter { get; }

    /// <summary><c>search</c>: substring matches.</summary>
    ZabbixGetConditions Search { get; }

    /// <summary><c>searchByAny</c>: the search terms are OR-ed.</summary>
    bool? SearchByAny { get; set; }

    /// <summary><c>startSearch</c>: the search matches at the start of the field.</summary>
    bool? StartSearch { get; set; }

    /// <summary><c>excludeSearch</c>: return what does not match.</summary>
    bool? ExcludeSearch { get; set; }

    /// <summary><c>searchWildcardsEnabled</c>: <c>*</c> in a search term is a wildcard.</summary>
    bool? SearchWildcardsEnabled { get; set; }

    /// <summary><c>limit</c>: at most this many.</summary>
    int? Limit { get; set; }

    /// <summary><c>countOutput</c>: return how many instead of which.</summary>
    bool? CountOutput { get; set; }
}
