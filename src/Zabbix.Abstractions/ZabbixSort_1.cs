// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Community.Zabbix;

/// <summary>
/// Represents one key of a get's sort: a field of <typeparamref name="TRecord"/> and its direction. A field converts to its
/// ascending sort, and <see cref="ZabbixField{TRecord}.Descending"/> makes the other:
/// <c>q.Sort = [ZabbixHostFields.VisibleName.Descending(), ZabbixHostFields.HostId]</c>.
/// </summary>
/// <remarks>
/// A get sends its sort as the API's <c>sortfield</c> and <c>sortorder</c>, two lists in step, and reads them back as one
/// list of these.
/// </remarks>
/// <typeparam name="TRecord">The record the field belongs to.</typeparam>
/// <param name="Field">The field to sort by.</param>
/// <param name="Direction">Whether the lowest value comes first or last.</param>
public readonly record struct ZabbixSort<TRecord>(ZabbixField<TRecord> Field, ListSortDirection Direction)
{
    /// <summary>Converts a field to its ascending sort.</summary>
    /// <param name="field">The field to sort by.</param>
    /// <returns>The sort, from the lowest value to the highest.</returns>
    public static implicit operator ZabbixSort<TRecord>(ZabbixField<TRecord> field) => FromZabbixField(field);

    /// <summary>Creates a field's ascending sort: the named form of the implicit conversion.</summary>
    /// <param name="field">The field to sort by.</param>
    /// <returns>The sort, from the lowest value to the highest.</returns>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "The named form of the implicit conversion, as ZabbixOptional<T>.FromNullable is; the type argument is inferred from the target.")]
    public static ZabbixSort<TRecord> FromZabbixField(ZabbixField<TRecord> field) => new(field, ListSortDirection.Ascending);

    /// <summary>Returns the field's name, and <c>DESC</c> after it for a descending sort.</summary>
    /// <returns>The sort as text.</returns>
    public override string ToString() => Direction == ListSortDirection.Descending ? Field.Name + " DESC" : Field.Name;
}

/// <summary>
/// The wire form of a get's sort: one list of <see cref="ZabbixSort{TRecord}"/> as the API's two lists in step,
/// <c>sortfield</c> and <c>sortorder</c>, a direction for every field. Read back, a <c>sortorder</c> shorter than
/// <c>sortfield</c> leaves the rest ascending, as Zabbix reads it.
/// </summary>
internal static class ZabbixSortWire
{
    /// <summary>The sort the two wire lists hold; <see langword="null"/> when there are no fields.</summary>
    /// <typeparam name="TRecord">The record the fields belong to.</typeparam>
    /// <param name="fields">The <c>sortfield</c> list.</param>
    /// <param name="directions">The <c>sortorder</c> list, its directions by position.</param>
    /// <returns>The sort.</returns>
    public static IReadOnlyList<ZabbixSort<TRecord>>? Join<TRecord>(IReadOnlyList<ZabbixField<TRecord>>? fields, IReadOnlyList<ListSortDirection>? directions)
    {
        if (fields is null) return null;
        var sort = new ZabbixSort<TRecord>[fields.Count];
        for (int i = 0; i < sort.Length; i++)
            sort[i] = new(fields[i], directions is not null && i < directions.Count ? directions[i] : ListSortDirection.Ascending);
        return sort;
    }

    /// <summary>The two wire lists a sort sends: its fields, and their directions in the same order.</summary>
    /// <typeparam name="TRecord">The record the fields belong to.</typeparam>
    /// <param name="sort">The sort to send.</param>
    /// <returns>The <c>sortfield</c> list and the <c>sortorder</c> list, both <see langword="null"/> when there's no sort.</returns>
    public static (IReadOnlyList<ZabbixField<TRecord>>? Fields, IReadOnlyList<ListSortDirection>? Directions) Split<TRecord>(IReadOnlyList<ZabbixSort<TRecord>>? sort)
    {
        if (sort is null) return (null, null);
        var fields = new ZabbixField<TRecord>[sort.Count];
        var directions = new ListSortDirection[sort.Count];
        for (int i = 0; i < fields.Length; i++) (fields[i], directions[i]) = (sort[i].Field, sort[i].Direction);
        return (fields, directions);
    }
}
