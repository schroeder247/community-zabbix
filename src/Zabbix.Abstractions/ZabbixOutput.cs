// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents the keyword a get's <c>output</c> and <c>select</c> options take, which names no field and so fits every
/// record: <see cref="Extend"/>, every field. It converts to the <see cref="ZabbixOutput{TRecord}"/> each option takes, so
/// <c>q.SelectInterfaces = ZabbixOutput.Extend</c> works for any option, and a list of fields is a collection expression of
/// the record's fields: <c>q.Output = [ZabbixHostFields.HostId, ZabbixHostFields.VisibleName]</c>.
/// </summary>
/// <remarks>
/// A <c>select</c> option's <c>count</c> keyword isn't here: it makes the option's field a number, which the record's list
/// can't hold, so a typed get doesn't send it (ADR 0013, section 4). <c>CountAsync</c> counts a get's records.
/// </remarks>
public sealed class ZabbixOutput
{
    private ZabbixOutput(string keyword) => Keyword = keyword;

    /// <summary>Gets <c>extend</c>: every field.</summary>
    public static ZabbixOutput Extend { get; } = new("extend");

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
