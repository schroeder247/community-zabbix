// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// The values a generated <c>.get</c> filter or search sends for one field, and the lookup its dictionary view answers
/// with. A filter's member is a list of the field's own type (an enum, a number, text); the API takes an array of text
/// or of integers, which is what <see cref="ZabbixFieldValue"/> holds.
/// </summary>
internal static class ZabbixFieldEntries
{
    /// <summary>The text values, as an array.</summary>
    /// <param name="values">The values the member holds, in order.</param>
    public static ZabbixFieldValue Of(IReadOnlyList<string> values) => ZabbixFieldValue.FromStringArray([.. values]);

    /// <summary>The values as integers, as an array.</summary>
    /// <typeparam name="T">The member's element type: an enum or an integer.</typeparam>
    /// <param name="values">The values the member holds, in order.</param>
    /// <param name="number">The value's integer on the wire.</param>
    public static ZabbixFieldValue Of<T>(IReadOnlyList<T> values, Func<T, long> number)
    {
        long[] numbers = new long[values.Count];
        for (int i = 0; i < numbers.Length; i++) numbers[i] = number(values[i]);
        return ZabbixFieldValue.FromInt64Array(numbers);
    }

    /// <summary>The values as text, as an array.</summary>
    /// <typeparam name="T">The member's element type: a number the wire writes as text.</typeparam>
    /// <param name="values">The values the member holds, in order.</param>
    /// <param name="text">The value's text on the wire.</param>
    public static ZabbixFieldValue Of<T>(IReadOnlyList<T> values, Func<T, string> text)
    {
        string[] texts = new string[values.Count];
        for (int i = 0; i < texts.Length; i++) texts[i] = text(values[i]);
        return ZabbixFieldValue.FromStringArray(texts);
    }
}
