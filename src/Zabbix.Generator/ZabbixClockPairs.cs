// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>
/// Folds a clock's nanoseconds into the clock. Zabbix writes an instant as two fields (<c>clock</c> and <c>ns</c>, an
/// item's <c>lastclock</c> and <c>lastns</c>, a problem's <c>r_clock</c> and <c>r_ns</c>); the description keeps both, as
/// the wire has them, and the record carries one <c>DateTimeOffset</c> member over the two. The
/// nanoseconds field leaves the record's fields, so every emitter (the records, the cmdlets, the views) sees the one
/// member.
/// </summary>
internal static class ZabbixClockPairs
{
    // A clock's wire name and its nanoseconds' (event.c, history.c, the item's lastclock/lastns columns).
    private static readonly (string Clock, string Nanoseconds)[] s_pairs = [("clock", "ns"), ("lastclock", "lastns"), ("r_clock", "r_ns")];

    /// <summary><paramref name="type"/> with each clock that has a nanoseconds field beside it carrying that field.</summary>
    /// <param name="type">The record.</param>
    public static TypeDescription Fold(TypeDescription type)
    {
        if (type is null) throw new ArgumentNullException(nameof(type));
        var folded = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<FieldDescription>(type.Fields.Length);
        foreach (FieldDescription field in type.Fields)
        {
            (string Clock, string Nanoseconds) pair = s_pairs.FirstOrDefault(p => p.Clock == field.Wire);
            if (field.Kind == "clock" && pair.Clock is not null
                && type.Fields.FirstOrDefault(f => f.Wire == pair.Nanoseconds && f.Kind == "int") is not null)
            {
                fields.Add(field with { Nanoseconds = pair.Nanoseconds });
                folded.Add(pair.Nanoseconds);
            }
            else
            {
                fields.Add(field);
            }
        }
        return folded.Count == 0 ? type : type with { Fields = [.. fields.Where(f => !folded.Contains(f.Wire))] };
    }
}
