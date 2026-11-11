// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell;

/// <summary>
/// The common <c>.get</c> options a selection sets before the entity's own parameters record exists: what the generic
/// code decides (a name search, an output restriction, a limit), handed to the record's generated factory, which alone
/// knows the record's required options and its fields' type, and builds the instance
/// (<see cref="ZabbixEntityShape{TEntity}.Materialize"/>).
/// </summary>
internal sealed record ZabbixGetSelection
{
    /// <summary>The fields to return, by the names the API gives them, or <see langword="null"/> for the server's default set.</summary>
    public IReadOnlyList<string>? Output { get; init; }

    /// <summary>An exact filter by field, or <see langword="null"/>.</summary>
    public IReadOnlyDictionary<string, ZabbixFieldValue>? Filter { get; init; }

    /// <summary>A search by field, or <see langword="null"/>.</summary>
    public IReadOnlyDictionary<string, ZabbixFieldValue>? Search { get; init; }

    /// <summary>Whether a search matches on any field rather than all.</summary>
    public bool? SearchByAny { get; init; }

    /// <summary>Whether a search is anchored at the start of the field.</summary>
    public bool? StartSearch { get; init; }

    /// <summary>Whether <c>*</c> in a search pattern is a wildcard.</summary>
    public bool? SearchWildcardsEnabled { get; init; }

    /// <summary>The most objects to return, or <see langword="null"/> for all.</summary>
    public int? Limit { get; init; }
}
