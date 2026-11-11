// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Globalization;
using System.Text;

namespace Community.Zabbix;

/// <summary>
/// The text form of a generated record, written by its <c>PrintMembers</c> (the hook the compiler's record
/// <c>ToString()</c> calls): a member that is not set is left out, a list or dictionary prints how many it holds, and a
/// secret prints <c>***</c> when it is set. A read record has some forty members and a reply sets a handful, so the
/// compiler's form (every member, an empty one as <c>Name = ,</c>, a list as its type name) says nothing useful.
/// </summary>
internal static class ZabbixRecordText
{
    /// <summary>Appends <paramref name="name"/> and <paramref name="value"/> when the value is set.</summary>
    /// <param name="builder">The text so far.</param>
    /// <param name="more">Whether a member was already written: a separator goes before the next; set when this one is.</param>
    /// <param name="name">The member's name.</param>
    /// <param name="value">The member's value.</param>
    public static void Member(StringBuilder builder, ref bool more, string name, object? value)
    {
        if (value is null) return;
        Start(builder, ref more, name);
        if (value is IFormattable formattable) builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
        else builder.Append(value);
    }

    /// <summary>Appends <paramref name="name"/> and how many entries <paramref name="value"/> holds, when it is set.</summary>
    /// <param name="builder">The text so far.</param>
    /// <param name="more">Whether a member was already written.</param>
    /// <param name="name">The member's name.</param>
    /// <param name="value">The list or dictionary.</param>
    public static void Count(StringBuilder builder, ref bool more, string name, IEnumerable? value)
    {
        if (value is null) return;
        int count = value switch
        {
            ICollection collection => collection.Count,
            _ => Counted(value),
        };
        Start(builder, ref more, name);
        builder.Append('[').Append(count.ToString(CultureInfo.InvariantCulture)).Append(count == 1 ? " item]" : " items]");
    }

    /// <summary>Appends <paramref name="name"/> and <c>***</c> when the secret is set: the value never reaches the text.</summary>
    /// <param name="builder">The text so far.</param>
    /// <param name="more">Whether a member was already written.</param>
    /// <param name="name">The member's name.</param>
    /// <param name="set">Whether the secret is set.</param>
    public static void Secret(StringBuilder builder, ref bool more, string name, bool set)
    {
        if (!set) return;
        Start(builder, ref more, name);
        builder.Append("***");
    }

    private static void Start(StringBuilder builder, ref bool more, string name)
    {
        if (more) builder.Append(", ");
        builder.Append(name).Append(" = ");
        more = true;
    }

    private static int Counted(IEnumerable value)
    {
        int count = 0;
        foreach (object? _ in value) count++;
        return count;
    }
}
