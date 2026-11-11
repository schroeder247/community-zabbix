// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents the parameters every queryable <c>.get</c> of <typeparamref name="TRecord"/> accepts: the eight of
/// <see cref="IZabbixGetParameters"/>, and the output and the sort, which name the record's fields. A host's
/// <c>ZabbixHostGetParameters</c> is an <c>IZabbixGetParameters&lt;ZabbixHost&gt;</c>, so its output and sort take only a
/// host's fields.
/// </summary>
/// <typeparam name="TRecord">The record the <c>.get</c> returns.</typeparam>
public interface IZabbixGetParameters<TRecord> : IZabbixGetParameters
{
    /// <summary>Gets or sets <c>output</c>: which fields come back.</summary>
    ZabbixOutput<TRecord>? Output { get; set; }

    /// <summary>
    /// Gets or sets the sort: the fields to order by, in order, each ascending unless it says descending, sent as
    /// <c>sortfield</c> and <c>sortorder</c>.
    /// </summary>
    IReadOnlyList<ZabbixSort<TRecord>>? Sort { get; set; }
}
