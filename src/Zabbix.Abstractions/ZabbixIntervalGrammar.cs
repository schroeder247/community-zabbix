// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Community.Zabbix;

/// <summary>
/// Reads Zabbix's grammar of time: an update interval, which <see cref="ZabbixInterval"/> checks a text against and an
/// agent schedules by, and the length of time <see cref="ZabbixDuration"/> holds. One reader for both, and for every
/// package: the API's records, the agent's schedule and the proxy's configuration read a delay alike.
/// </summary>
/// <remarks>
/// <para>
/// A length is a whole number of seconds, written bare or with an <c>s</c>, <c>m</c>, <c>h</c>, <c>d</c> or <c>w</c>
/// suffix, within what Zabbix's server reads as seconds, or a macro (<c>CSimpleIntervalParser.php</c> 7.0;
/// <c>zbx_is_time_suffix</c> in <c>src/libs/zbxtime/time.c</c>, which also takes a leading zero). An update interval is a
/// length, then any number of flexible or scheduling intervals, each after a <c>;</c> (<c>CUpdateIntervalParser.php</c> 7.0,
/// <c>src/libs/zbxexpr/interval.c</c>). A flexible interval is a length, a <c>/</c> and a period, <c>d[-d],hh:mm-hh:mm</c>
/// or a macro (<c>CFlexibleIntervalParser.php</c>, <c>CTimePeriodParser.php</c> 7.0); a scheduling interval is a macro, or
/// filters of month days, week days, hours, minutes and seconds in that order, each <c>from[-to][/step]</c> or
/// <c>/step</c>, separated by commas (<c>CSchedulingIntervalParser.php</c> 7.0). A macro is a user or LLD macro, maybe in a
/// macro function, and is kept as it's written.
/// </para>
/// <para>
/// The reader checks the grammar only. What a value may be is the reader's caller's: the server's ranges for the field, and
/// an agent's own rules for what it schedules (no macro, an update interval of at most a day).
/// </para>
/// </remarks>
internal static class ZabbixIntervalGrammar
{
    /// <summary>Seconds in a minute.</summary>
    public const long Minute = 60;

    /// <summary>Seconds in an hour.</summary>
    public const long Hour = 60 * Minute;

    /// <summary>Seconds in a day.</summary>
    public const long Day = 24 * Hour;

    /// <summary>Seconds in a week.</summary>
    public const long Week = 7 * Day;

    /// <summary>Seconds in a month, as Zabbix counts one where a length takes it (<c>SEC_PER_MONTH</c>).</summary>
    public const long Month = 30 * Day;

    /// <summary>Seconds in a year, as Zabbix counts one where a length takes it (<c>SEC_PER_YEAR</c>).</summary>
    public const long Year = 365 * Day;

    /// <summary>The finest unit a scheduling interval filters by.</summary>
    internal enum Level
    {
        None = 0,
        Day = 1,
        Hour = 2,
        Minute = 3,
        Second = 4,
    }

    /// <summary>Which part of an update interval a text fails at.</summary>
    internal enum Failure
    {
        Update,
        Flexible,
        Scheduling,
    }

    /// <summary>The length at the start of <paramref name="s"/>: seconds, or <see langword="null"/> for a macro; 0 characters read when it has none.</summary>
    /// <param name="s">The text to read, from its first character.</param>
    /// <param name="length">How many characters the length takes; 0 when <paramref name="s"/> doesn't start with one.</param>
    public static long? Length(ReadOnlySpan<char> s, out int length) => Length(s, withYear: false, out length);

    /// <summary>
    /// The length at the start of <paramref name="s"/>, where a field also takes a month (<c>M</c>, 30 days) and a year
    /// (<c>y</c>, 365 days), as the settings' <c>max_period</c> does (<c>API_TIME_UNIT_WITH_YEAR</c>,
    /// <c>ZBX_TIME_SUFFIXES_WITH_YEAR</c> in <c>defines.inc.php</c> 7.0): seconds, or <see langword="null"/> for a macro.
    /// </summary>
    /// <param name="s">The text to read, from its first character.</param>
    /// <param name="length">How many characters the length takes; 0 when <paramref name="s"/> doesn't start with one.</param>
    public static long? LengthWithYear(ReadOnlySpan<char> s, out int length) => Length(s, withYear: true, out length);

    private static long? Length(ReadOnlySpan<char> s, bool withYear, out int length)
    {
        if (Seconds(s, withYear, out length) is { } seconds) return seconds;
        length = ZabbixMacroGrammar.UserOrLldMacro(s);
        return null;
    }

    /// <summary>Reads an update interval into its parts, or says which part it fails at.</summary>
    /// <param name="s">The update interval as Zabbix writes it.</param>
    /// <param name="parts">The parts, when <paramref name="s"/> is an update interval.</param>
    /// <param name="failure">The part that fails, when it isn't.</param>
    /// <param name="segment">The text of the part that fails: up to the next <c>;</c>.</param>
    /// <returns>Whether <paramref name="s"/> is an update interval.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, [NotNullWhen(true)] out Parts? parts, out Failure failure, out string segment)
    {
        parts = null;
        failure = Failure.Update;
        int p = 0;
        long? update = Length(s, out int read);
        p += read;
        if (read == 0 || (p < s.Length && s[p] != ';'))
        {
            segment = Segment(s, 0);
            return false;
        }
        string updateText = s[..p].ToString();
        var customs = new List<Custom>();
        while (p < s.Length)
        {
            int start = ++p;                                                                // past the ';'
            if (Flexible(s, ref p, out long? delay, out Period? period) && Ends(s, p))
            {
                customs.Add(new(s[start..p].ToString(), IsFlexible: true, delay, period, null));
                continue;
            }
            p = start;
            if (Scheduling(s, ref p, out Schedule? schedule) && Ends(s, p))
            {
                customs.Add(new(s[start..p].ToString(), IsFlexible: false, null, null, schedule));
                continue;
            }
            segment = Segment(s, start);
            failure = segment.Length > 0 && (char.IsAsciiDigit(segment[0]) || (segment[0] == '{' && segment.Contains('/', StringComparison.Ordinal))) ? Failure.Flexible : Failure.Scheduling;
            return false;
        }
        parts = new(updateText, update, [.. customs]);
        segment = string.Empty;
        return true;
    }

    // The end of a part: the end of the text, or a ';' before the next.
    private static bool Ends(ReadOnlySpan<char> s, int p) => p == s.Length || s[p] == ';';

    // The text from start to the next ';'.
    private static string Segment(ReadOnlySpan<char> s, int start)
    {
        int end = s[start..].IndexOf(';');
        return (end < 0 ? s[start..] : s.Slice(start, end)).ToString();
    }

    // A whole number of seconds, bare or with a suffix, within int.MaxValue seconds.
    private static long? Seconds(ReadOnlySpan<char> s, bool withYear, out int length)
    {
        length = 0;
        int p = 0;
        long value = 0;
        while (p < s.Length && char.IsAsciiDigit(s[p]))
        {
            value = (value * 10) + (s[p++] - '0');
            if (value > int.MaxValue) return null;
        }
        if (p == 0) return null;
        long factor = p < s.Length ? s[p] switch { 's' => 1, 'm' => Minute, 'h' => Hour, 'd' => Day, 'w' => Week, 'M' when withYear => Month, 'y' when withYear => Year, _ => 0 } : 0;
        if (factor > 0) p++;
        long seconds = value * Math.Max(factor, 1);
        if (seconds > int.MaxValue) return null;
        length = p;
        return seconds;
    }

    // length/period: the period a macro, or d[-d],h:mm-h:mm, its days 1 (Monday) to 7, its end after its start and at
    // most 24:00.
    private static bool Flexible(ReadOnlySpan<char> s, ref int p, out long? delay, out Period? period)
    {
        period = null;
        delay = Length(s[p..], out int read);
        p += read;
        if (read == 0 || p >= s.Length || s[p] != '/') return false;
        p++;
        int macro = ZabbixMacroGrammar.UserOrLldMacro(s[p..]);
        if (macro > 0)
        {
            p += macro;
            return true;
        }
        int q = p;
        if (!WeekDay(s, ref q, out int fromDay)) return false;
        int tillDay = fromDay;
        if (q < s.Length && s[q] == '-')
        {
            q++;
            if (!WeekDay(s, ref q, out tillDay)) return false;
        }
        if (fromDay > tillDay || q >= s.Length || s[q] != ',') return false;
        q++;
        if (!Time(s, ref q, out int from) || q >= s.Length || s[q] != '-') return false;
        q++;
        if (!Time(s, ref q, out int till) || from >= till || till > Day) return false;
        period = new(fromDay, tillDay, from, till);
        p = q;
        return true;
    }

    private static bool WeekDay(ReadOnlySpan<char> s, ref int p, out int day)
    {
        day = p < s.Length && s[p] is >= '1' and <= '7' ? s[p] - '0' : 0;
        if (day == 0) return false;
        p++;
        return true;
    }

    // h:mm or hh:mm, in seconds of the day; the minutes 00 to 59.
    private static bool Time(ReadOnlySpan<char> s, ref int p, out int seconds)
    {
        seconds = 0;
        int q = p;
        int hours = 0;
        while (q < s.Length && q - p < 2 && char.IsAsciiDigit(s[q])) hours = (hours * 10) + (s[q++] - '0');
        if (q == p || q + 2 >= s.Length || s[q] != ':' || !char.IsAsciiDigit(s[q + 1]) || !char.IsAsciiDigit(s[q + 2])) return false;
        int minutes = ((s[q + 1] - '0') * 10) + (s[q + 2] - '0');
        if (minutes > 59) return false;
        p = q + 3;
        seconds = (int)((hours * Hour) + (minutes * Minute));
        return true;
    }

    // A scheduling interval: a macro, or filters of each unit at most once, in order.
    private static bool Scheduling(ReadOnlySpan<char> s, ref int p, out Schedule? schedule)
    {
        schedule = null;
        int macro = ZabbixMacroGrammar.UserOrLldMacro(s[p..]);
        if (macro > 0)
        {
            p += macro;
            return true;
        }
        int start = p;
        var filters = new Filter[]?[s_units.Length];
        int next = 0;
        while (p < s.Length)
        {
            int found = -1;
            for (int i = next; i < s_units.Length && found < 0; i++)
            {
                if (Filters(s, ref p, s_units[i], out filters[i])) found = i;
            }
            if (found < 0) break;
            next = found + 1;
        }
        if (p == start) return false;
        Level level = filters[4] is not null ? Level.Second : filters[3] is not null ? Level.Minute : filters[2] is not null ? Level.Hour : Level.Day;
        schedule = new(filters[0], filters[1], filters[2], filters[3], filters[4], level);
        return true;
    }

    // The units of a scheduling interval, in the order they're written, and the values each takes.
    private static readonly ImmutableArray<(string Prefix, int Min, int Max, int Width)> s_units = [("md", 1, 31, 2), ("wd", 1, 7, 1), ("h", 0, 23, 2), ("m", 0, 59, 2), ("s", 0, 59, 2)];

    // One unit's filters: from[-to][/step], or /step over the whole unit; after the first, each starts with a comma.
    private static bool Filters(ReadOnlySpan<char> s, ref int p, (string Prefix, int Min, int Max, int Width) unit, out Filter[]? filters)
    {
        filters = null;
        if (!s[p..].StartsWith(unit.Prefix, StringComparison.Ordinal)) return false;
        int start = p + unit.Prefix.Length;
        int q = start;
        bool comma = false;
        var read = new List<Filter>();
        while (q < s.Length)
        {
            int length = 0;
            (int from, int to) = (unit.Min, unit.Max);
            int at = q;
            if (Range(s, comma, ref at, unit.Width, out int first, out int last))
            {
                if (first < unit.Min || last > unit.Max || first > last) break;
                (from, to) = (first, last);
                length = at - q;
                comma = false;
            }
            int step = 1;
            if (Step(s, comma, q + length, unit.Width, out int every, out int stepLength) && every >= 1 && every <= to - from)
            {
                length += stepLength;
                step = every;
            }
            if (length == 0) break;
            read.Add(new(from, to, step));
            q += length;
            comma = true;
        }
        if (q == start) return false;
        filters = [.. read];
        p = q;
        return true;
    }

    // [,]from[-to] at p, moving p past it.
    private static bool Range(ReadOnlySpan<char> s, bool comma, ref int p, int width, out int from, out int to)
    {
        to = 0;
        int q = p;
        if (comma && (q >= s.Length || s[q++] != ','))
        {
            from = 0;
            return false;
        }
        if (!Number(s, q, width, out from, out int length)) return false;
        q += length;
        to = from;
        if (q < s.Length && s[q] == '-' && Number(s, q + 1, width, out int last, out int lastLength))
        {
            to = last;
            q += 1 + lastLength;
        }
        p = q;
        return true;
    }

    // [,]/step at p.
    private static bool Step(ReadOnlySpan<char> s, bool comma, int p, int width, out int step, out int length)
    {
        step = 0;
        length = 0;
        int q = p;
        if (comma && (q >= s.Length || s[q++] != ',')) return false;
        if (q >= s.Length || s[q] != '/' || !Number(s, q + 1, width, out step, out int digits)) return false;
        length = q + 1 + digits - p;
        return true;
    }

    // One to width digits at p.
    private static bool Number(ReadOnlySpan<char> s, int p, int width, out int value, out int length)
    {
        value = 0;
        length = 0;
        while (p + length < s.Length && length < width && char.IsAsciiDigit(s[p + length]))
        {
            value = (value * 10) + (s[p + length] - '0');
            length++;
        }
        return length > 0;
    }

    /// <summary>One filter of a scheduling interval: <see cref="Start"/> to <see cref="End"/>, every <see cref="Step"/>.</summary>
    /// <param name="Start">The first value.</param>
    /// <param name="End">The last value.</param>
    /// <param name="Step">The distance between two values it matches.</param>
    internal readonly record struct Filter(int Start, int End, int Step);

    /// <summary>Days 1 (Monday) to 7, and a time range in seconds of the day, its end excluded.</summary>
    /// <param name="StartDay">The first day.</param>
    /// <param name="EndDay">The last day.</param>
    /// <param name="StartTime">The start, in seconds of the day.</param>
    /// <param name="EndTime">The end, in seconds of the day.</param>
    internal readonly record struct Period(int StartDay, int EndDay, int StartTime, int EndTime);

    /// <summary>A scheduling interval's filters by unit, <see langword="null"/> for a unit it doesn't filter, and its finest unit.</summary>
    /// <param name="MonthDays">The days of the month.</param>
    /// <param name="WeekDays">The days of the week.</param>
    /// <param name="Hours">The hours of the day.</param>
    /// <param name="Minutes">The minutes of the hour.</param>
    /// <param name="Seconds">The seconds of the minute.</param>
    /// <param name="Level">The finest unit it filters.</param>
    internal sealed record Schedule(Filter[]? MonthDays, Filter[]? WeekDays, Filter[]? Hours, Filter[]? Minutes, Filter[]? Seconds, Level Level);

    /// <summary>
    /// One interval after a <c>;</c>: its text, and a flexible interval's length and period or a scheduling interval's
    /// filters, each <see langword="null"/> where a macro stands.
    /// </summary>
    /// <param name="Text">The interval as written.</param>
    /// <param name="IsFlexible">Whether it's a flexible interval, not a scheduling one.</param>
    /// <param name="Delay">A flexible interval's length, in seconds.</param>
    /// <param name="Period">A flexible interval's period.</param>
    /// <param name="Schedule">A scheduling interval's filters.</param>
    internal sealed record Custom(string Text, bool IsFlexible, long? Delay, Period? Period, Schedule? Schedule);

    /// <summary>An update interval's parts: its first length, as written and in seconds (<see langword="null"/> for a macro), and the intervals after it.</summary>
    /// <param name="Update">The update interval as written.</param>
    /// <param name="UpdateSeconds">The update interval in seconds.</param>
    /// <param name="Customs">The flexible and scheduling intervals, in order.</param>
    internal sealed record Parts(string Update, long? UpdateSeconds, Custom[] Customs);
}
