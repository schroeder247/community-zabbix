// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents the keywords a get's <c>output</c> and <c>select</c> options take, which name no field and so fit every
/// record: <see cref="Extend"/>, every field, and <see cref="Count"/>, how many. A keyword converts to the
/// <see cref="ZabbixOutput{TRecord}"/> each option takes, so <c>q.SelectInterfaces = ZabbixOutput.Extend</c> works for any
/// option, and a list of fields is a collection expression of the record's fields:
/// <c>q.Output = [ZabbixHostFields.HostId, ZabbixHostFields.VisibleName]</c>.
/// </summary>
public sealed class ZabbixOutput
{
    private ZabbixOutput(string keyword) => Keyword = keyword;

    /// <summary>Gets <c>extend</c>: every field.</summary>
    public static ZabbixOutput Extend { get; } = new("extend");

    /// <summary>Gets <c>count</c>: how many, where a <c>select</c> option takes a count.</summary>
    public static ZabbixOutput Count { get; } = new("count");

    /// <summary>Gets the keyword, as the API spells it.</summary>
    public string Keyword { get; }

    /// <summary>Creates the output that names <paramref name="fields"/>; a collection expression of fields calls it.</summary>
    /// <typeparam name="TRecord">The record the fields belong to.</typeparam>
    /// <param name="fields">The fields, in the order they're sent.</param>
    /// <returns>The output that names the fields.</returns>
    /// <exception cref="ArgumentException">A field is the default value, which names no field.</exception>
    public static ZabbixOutput<TRecord> Of<TRecord>(params ReadOnlySpan<ZabbixField<TRecord>> fields) => ZabbixOutput<TRecord>.FromFields(fields);

    /// <summary>Returns the keyword, as the API spells it.</summary>
    /// <returns>The keyword.</returns>
    public override string ToString() => Keyword;
}
