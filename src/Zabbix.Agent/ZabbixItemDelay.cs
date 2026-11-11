// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;

namespace Community.Zabbix;

/// <summary>
/// Represents an item's update interval, as Zabbix writes it and schedules it. <c>30s</c> is a plain update interval;
/// <c>50s;10s/1-5,09:00-18:00</c> adds a flexible interval (every 10 s on weekdays in office hours); <c>0;wd1-5h9m30</c>
/// is a scheduling interval (weekdays at 09:30, and nothing else). <see cref="GetNextCheck(DateTimeOffset, TimeZoneInfo, long)"/> answers when the item is due.
/// </summary>
/// <remarks>
/// <para>The grammar and the schedule mirror Zabbix's own C (<c>src/libs/zbxexpr/interval.c</c>, identical in 7.0, 7.4
/// and 8.0; <c>zbx_is_time_suffix</c> in <c>src/libs/zbxtime/time.c</c>), because an agent that disagrees collects at
/// instants the server does not expect; the golden tests are upstream's <c>tests/libs/zbxexpr/*.yaml</c>. User macros
/// are resolved by the server before an agent sees the delay.</para>
/// <para>No BCL type parses or schedules this grammar, and the cron libraries cannot express a flexible interval and
/// resolve a daylight-saving gap or overlap differently from Zabbix's <c>mktime</c> <c>tm_isdst</c> hint. Only the parser
/// and the next-check arithmetic are owned; time zones, calendars and the clock are the BCL's.</para>
/// </remarks>
public sealed class ZabbixItemDelay : IEquatable<ZabbixItemDelay>, ISpanParsable<ZabbixItemDelay>, ISpanFormattable
{
    private const int SecondsPerMinute = 60;
    private const int SecondsPerHour = 3600;
    private const int SecondsPerDay = 86400;
    private const int SecondsPerWeek = 7 * SecondsPerDay;
    private const long SecondsPerYear = 365L * SecondsPerDay;

    private readonly Flexible[] _flexible;
    private readonly Scheduling[] _scheduling;

    private ZabbixItemDelay(string text, int updateInterval, Flexible[] flexible, Scheduling[] scheduling)
    {
        Text = text;
        UpdateSeconds = updateInterval;
        _flexible = flexible;
        _scheduling = scheduling;
    }

    /// <summary>The delay as written.</summary>
    public string Text { get; }

    /// <summary>The update interval before the first <c>;</c>: the cadence outside every flexible interval. Zero means "only when a custom interval says so".</summary>
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(UpdateSeconds);

    /// <summary>Whether the delay has a scheduling interval (<c>wd1-5h9</c>); upstream's <c>zbx_custom_interval_is_scheduling</c>.</summary>
    public bool IsScheduling => _scheduling.Length > 0;

    private int UpdateSeconds { get; }

    /// <summary>
    /// When an item with no phase is due after <paramref name="now"/>: <see cref="GetNextCheck(DateTimeOffset, TimeZoneInfo, long)"/>
    /// with a seed of 0, so every item with the same delay falls due at the same instants.
    /// </summary>
    /// <param name="now">The instant to schedule from.</param>
    /// <param name="zone">The zone flexible and scheduling intervals are read in: the agent's local time.</param>
    /// <returns>The next instant, or <see langword="null"/> when the delay never falls due.</returns>
    public DateTimeOffset? GetNextCheck(DateTimeOffset now, TimeZoneInfo zone) => GetNextCheck(now, zone, 0);

    /// <summary>
    /// When the item is due after <paramref name="now"/>: upstream's <c>zbx_calculate_item_nextcheck</c> for a polled
    /// item, which is what both agents compute for an active check (<c>zbx_get_agent_item_nextcheck</c>). The cadence is
    /// phased by <paramref name="seed"/> (the item id), so items with the same delay spread over it; a flexible interval
    /// replaces the cadence while its period lasts; a scheduling interval adds its own instants. Resolution is one second.
    /// </summary>
    /// <param name="now">The instant to schedule from.</param>
    /// <param name="zone">The zone flexible and scheduling intervals are read in: the agent's local time.</param>
    /// <param name="seed">The phase: the item id (read as the unsigned 64-bit value upstream's <c>zbx_uint64_t</c> itemid is).</param>
    /// <returns>The next instant, or <see langword="null"/> when the delay never falls due (upstream's
    /// <c>ZBX_JAN_2038</c>): every interval within the next year has a delay of 0 and nothing is scheduled.</returns>
    public DateTimeOffset? GetNextCheck(DateTimeOffset now, TimeZoneInfo zone, long seed)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var clock = new Clock(zone);
        long at = now.ToUnixTimeSeconds();
        long scheduled = _scheduling.Length > 0 ? clock.ScheduledNextCheck(_scheduling, at) : 0;

        long nextcheck = Never;
        long t = at;
        long tmax = at + SecondsPerYear;
        int attempt = 0;
        while (t < tmax)
        {
            int delay = CurrentDelay(clock, t);
            if (delay != 0)
            {
                nextcheck = (delay * FloorDiv(t, delay)) + (long)(unchecked((ulong)seed) % (ulong)delay);
                if (attempt == 0)
                    while (nextcheck <= t) nextcheck += delay;
                else
                    while (nextcheck < t) nextcheck += delay;
            }
            else
            {
                nextcheck = Never;
            }

            // Past the end of the flexible interval in force: schedule from the start of the next one instead.
            if (NextDelayInterval(clock, t) is { } next && nextcheck >= next)
            {
                t = next;
                attempt++;
            }
            else
            {
                break;
            }
        }

        if (scheduled != 0 && scheduled < nextcheck) nextcheck = scheduled;
        return nextcheck == Never ? null : DateTimeOffset.FromUnixTimeSeconds(nextcheck);
    }

    /// <summary>Upstream's "far future" sentinel, kept as its own value rather than a date.</summary>
    private const long Never = long.MaxValue;

    // ----- parsing: zbx_interval_preproc ------------------------------------------------------------------------------

    /// <inheritdoc/>
    /// <exception cref="FormatException"><paramref name="s"/> is not a Zabbix update interval; the message is upstream's.</exception>
    public static ZabbixItemDelay Parse(string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan(), provider);
    }

    /// <inheritdoc/>
    /// <exception cref="FormatException"><paramref name="s"/> is not a Zabbix update interval; the message is upstream's.</exception>
    public static ZabbixItemDelay Parse(ReadOnlySpan<char> s, IFormatProvider? provider)
        => TryParse(s, out ZabbixItemDelay? result, out string? error) ? result : throw new FormatException(error);

    /// <inheritdoc/>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out ZabbixItemDelay result)
    {
        if (s is null)
        {
            result = null;
            return false;
        }
        return TryParse(s.AsSpan(), out result, out _);
    }

    /// <inheritdoc/>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, [MaybeNullWhen(false)] out ZabbixItemDelay result)
        => TryParse(s, out result, out _);

    /// <summary>
    /// Parses <paramref name="s"/> and, when it is not a delay, says why in upstream's words
    /// (<c>Invalid update interval "…".</c>, <c>Invalid flexible interval "…".</c>, <c>Invalid scheduling interval "…".</c>),
    /// which is what an agent reports as the item's not-supported reason.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="result">The delay.</param>
    /// <param name="error">Why it is not one.</param>
    /// <returns>Whether it parsed.</returns>
    internal static bool TryParse(ReadOnlySpan<char> s, [MaybeNullWhen(false)] out ZabbixItemDelay result, [NotNullWhen(false)] out string? error)
    {
        result = null;
        ReadOnlySpan<char> segment = s;
        int delimiter = s.IndexOf(';');
        if (!TryParseTimeSuffix(delimiter < 0 ? s : s[..delimiter], out int simple))
        {
            error = Invalid("update", delimiter < 0 ? s : s[..delimiter]);
            return false;
        }

        var flexible = new List<Flexible>();
        var scheduling = new List<Scheduling>();
        ReadOnlySpan<char> rest = s;
        while (delimiter >= 0)
        {
            rest = rest[(delimiter + 1)..];
            delimiter = rest.IndexOf(';');
            segment = delimiter < 0 ? rest : rest[..delimiter];
            if (segment.Length > 0 && char.IsAsciiDigit(segment[0]))
            {
                if (!Flexible.TryParse(segment, out Flexible interval) || (simple == 0 && interval.Delay == 0))
                {
                    error = Invalid("flexible", segment);
                    return false;
                }
                flexible.Add(interval);
            }
            else
            {
                if (!Scheduling.TryParse(segment, out Scheduling? interval))
                {
                    error = Invalid("scheduling", segment);
                    return false;
                }
                scheduling.Add(interval);
            }
        }

        if ((flexible.Count == 0 && scheduling.Count == 0 && simple == 0) || simple > SecondsPerDay)
        {
            error = Invalid("update", segment);                            // upstream names the last segment it read
            return false;
        }

        // Upstream prepends to its lists; the order only decides ties that give the same instant, so it is kept as read.
        result = new ZabbixItemDelay(s.ToString(), simple, [.. flexible], [.. scheduling]);
        error = null;
        return true;
    }

    private static string Invalid(string kind, ReadOnlySpan<char> text) => $"Invalid {kind} interval \"{text}\".";

    /// <summary>
    /// A non-negative whole number of seconds with an optional <c>s</c>, <c>m</c>, <c>h</c>, <c>d</c> or <c>w</c> suffix,
    /// the whole span and nothing else, within <see cref="int.MaxValue"/> seconds: upstream's <c>zbx_is_time_suffix</c>.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="seconds">The value in seconds.</param>
    /// <returns>Whether it parsed.</returns>
    internal static bool TryParseTimeSuffix(ReadOnlySpan<char> text, out int seconds)
    {
        seconds = 0;
        if (text.IsEmpty || !char.IsAsciiDigit(text[0])) return false;
        int value = 0;
        int i = 0;
        for (; i < text.Length && char.IsAsciiDigit(text[i]); i++)
        {
            int digit = text[i] - '0';
            if ((int.MaxValue - digit) / 10 < value) return false;                  // overflow
            value = (value * 10) + digit;
        }
        int factor = 1;
        if (i < text.Length)
        {
            factor = text[i] switch
            {
                's' => 1,
                'm' => SecondsPerMinute,
                'h' => SecondsPerHour,
                'd' => SecondsPerDay,
                'w' => SecondsPerWeek,
                _ => 0,
            };
            if (factor == 0) return false;
            i++;
        }
        if (i != text.Length || int.MaxValue / factor < value) return false;
        seconds = value * factor;
        return true;
    }

    /// <summary>Digits only, at least one, the value within [<paramref name="min"/>, <paramref name="max"/>]: <c>zbx_is_uint_n_range</c>.</summary>
    private static bool TryParseRange(ReadOnlySpan<char> digits, int min, int max, out int value)
    {
        value = 0;
        if (digits.IsEmpty) return false;
        long accumulated = 0;
        foreach (char c in digits)
        {
            if (!char.IsAsciiDigit(c)) return false;
            accumulated = (accumulated * 10) + (c - '0');
            if (accumulated > int.MaxValue) return false;
        }
        if (accumulated < min || accumulated > max) return false;
        value = (int)accumulated;
        return true;
    }

    // ----- formatting and equality ------------------------------------------------------------------------------------

    /// <summary>The delay as written.</summary>
    public override string ToString() => Text;

    /// <summary>The delay as written: the only format is the general one (<see langword="null"/>, empty or <c>G</c>).</summary>
    /// <param name="format">The format: <see langword="null"/>, empty or <c>G</c>.</param>
    /// <param name="formatProvider">Ignored: the text is culture-invariant.</param>
    /// <exception cref="FormatException"><paramref name="format"/> is another format.</exception>
    public string ToString(string? format, IFormatProvider? formatProvider) => IsGeneral(format) ? Text : throw UnknownFormat(format);

    /// <inheritdoc/>
    /// <exception cref="FormatException"><paramref name="format"/> is not empty or <c>G</c>.</exception>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (!IsGeneral(format)) throw UnknownFormat(format.ToString());
        if (Text.TryCopyTo(destination))
        {
            charsWritten = Text.Length;
            return true;
        }
        charsWritten = 0;
        return false;
    }

    private static bool IsGeneral(ReadOnlySpan<char> format) => format.IsEmpty || format is "G" or "g";

    private static FormatException UnknownFormat(string? format) => new($"The format '{format}' is not a delay format; a delay formats only as written (G).");

    /// <summary>Delays are equal when they are written the same (ordinal): the text is what the server holds.</summary>
    /// <param name="other">The other delay.</param>
    public bool Equals(ZabbixItemDelay? other) => other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ZabbixItemDelay other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    // ----- flexible intervals: get_current_delay, get_next_delay_interval ------------------------------------------------

    /// <summary>The smallest delay among the flexible intervals in force at <paramref name="t"/>, else the update interval.</summary>
    private int CurrentDelay(Clock clock, long t)
    {
        if (_flexible.Length == 0) return UpdateSeconds;
        LocalTime local = clock.Local(t);
        int current = -1;
        foreach (Flexible interval in _flexible)
            if ((current == -1 || interval.Delay < current) && interval.Period.Contains(local)) current = interval.Delay;
        return current == -1 ? UpdateSeconds : current;
    }

    /// <summary>When the next flexible period starts or the one in force ends, whichever is first; <see langword="null"/> without flexible intervals.</summary>
    private long? NextDelayInterval(Clock clock, long t)
    {
        if (_flexible.Length == 0) return null;
        LocalTime local = clock.Local(t);
        int day = local.IsoDayOfWeek;
        int time = local.SecondOfDay;
        int next = 0;
        foreach (Flexible interval in _flexible)
        {
            Period p = interval.Period;
            int candidate;
            if (p.StartDay <= day && day <= p.EndDay && time < p.EndTime)            // will be active today
                candidate = time < p.StartTime ? p.StartTime : p.EndTime;
            else if (day < p.EndDay)                                                // will be active this week
                candidate = day < p.StartDay ? (SecondsPerDay * (p.StartDay - day)) + p.StartTime : SecondsPerDay + p.StartTime;
            else                                                                    // will be active next week
                candidate = (SecondsPerDay * (p.StartDay + 7 - day)) + p.StartTime;
            if (next == 0 || next > candidate) next = candidate;
        }
        return next == 0 ? null : t - time + next;
    }

    private static long FloorDiv(long value, long divisor) => (value / divisor) - (value % divisor != 0 && (value < 0) != (divisor < 0) ? 1 : 0);

    /// <summary><c>delay/d[-d],hh:mm-hh:mm</c>: <c>flexible_interval_parse</c>.</summary>
    private readonly record struct Flexible(int Delay, Period Period)
    {
        public static bool TryParse(ReadOnlySpan<char> text, out Flexible result)
        {
            result = default;
            int slash = text.IndexOf('/');
            if (!TryParseTimeSuffix(slash < 0 ? text : text[..slash], out int delay) || slash < 0) return false;
            if (!Period.TryParse(text[(slash + 1)..], out Period period)) return false;
            result = new Flexible(delay, period);
            return true;
        }
    }

    /// <summary>Days 1 (Monday) to 7, and a time range in seconds of the day, end excluded: <c>zbx_time_period_t</c>.</summary>
    private readonly record struct Period(int StartDay, int EndDay, int StartTime, int EndTime)
    {
        public bool Contains(LocalTime local)
            => StartDay <= local.IsoDayOfWeek && local.IsoDayOfWeek <= EndDay && StartTime <= local.SecondOfDay && local.SecondOfDay < EndTime;

        /// <summary><c>time_period_parse</c>: <c>d[-d],time-time</c>.</summary>
        public static bool TryParse(ReadOnlySpan<char> text, out Period result)
        {
            result = default;
            int i = 0;
            if (i >= text.Length || text[i] < '1' || text[i] > '7') return false;
            int startDay = text[i++] - '0';
            if (i >= text.Length) return false;
            int endDay = startDay;
            if (text[i] == '-')
            {
                i++;
                if (i >= text.Length || text[i] < '1' || text[i] > '7') return false;
                endDay = text[i++] - '0';
                if (startDay > endDay) return false;
            }
            if (i >= text.Length || text[i++] != ',') return false;
            if (!TryParseTime(text[i..], out int startTime, out int parsed)) return false;
            i += parsed;
            if (i >= text.Length || text[i++] != '-') return false;
            if (!TryParseTime(text[i..], out int endTime, out parsed)) return false;
            if (startTime >= endTime || i + parsed != text.Length) return false;
            result = new Period(startDay, endDay, startTime, endTime);
            return true;
        }

        /// <summary><c>time_parse</c>: <c>h:mm</c>, <c>hh:mm</c> (hours 0–24, 24 only as 24:00), minutes exactly two digits.</summary>
        private static bool TryParseTime(ReadOnlySpan<char> text, out int seconds, out int parsed)
        {
            seconds = 0;
            parsed = 0;
            int i = 0;
            while (i < text.Length && char.IsAsciiDigit(text[i]) && i <= 2) i++;
            if (!TryParseRange(text[..i], 0, 24, out int hours)) return false;
            if (i >= text.Length || text[i++] != ':') return false;
            int minutesStart = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]) && i - minutesStart <= 2) i++;
            if (i - minutesStart != 2 || !TryParseRange(text[minutesStart..i], 0, 59, out int minutes)) return false;
            if (hours == 24 && minutes != 0) return false;
            parsed = i;
            seconds = (SecondsPerHour * hours) + (SecondsPerMinute * minutes);
            return true;
        }
    }

    // ----- scheduling intervals: scheduler_interval_parse and scheduler_get_nextcheck ----------------------------------

    /// <summary>One filter of a scheduling interval: <c>start[-end][/step]</c>.</summary>
    private readonly record struct Filter(int Start, int End, int Step);

    private enum Level
    {
        None = 0,
        Day = 1,
        Hour = 2,
        Minute = 3,
        Second = 4,
    }

    /// <summary>A scheduling interval: filters for month days or week days, hours, minutes and seconds.</summary>
    private sealed class Scheduling
    {
        public Filter[]? MonthDays { get; private set; }

        public Filter[]? WeekDays { get; private set; }

        public Filter[]? Hours { get; private set; }

        public Filter[]? Minutes { get; private set; }

        public Filter[]? Seconds { get; private set; }

        public Level FilterLevel { get; private set; }

        public static bool TryParse(ReadOnlySpan<char> text, [NotNullWhen(true)] out Scheduling? result)
        {
            result = null;
            if (text.IsEmpty) return false;
            var interval = new Scheduling();
            int i = 0;
            while (i < text.Length)
            {
                Filter[]? filters;
                switch (text[i])
                {
                    case 'h':
                        if (Level.Hour < interval.FilterLevel || interval.Hours is not null) return false;
                        if (!TryParseFilters(text, ref i, 1, 0, 23, 2, out filters)) return false;
                        interval.Hours = filters;
                        interval.FilterLevel = Level.Hour;
                        break;
                    case 's':
                        if (Level.Second < interval.FilterLevel || interval.Seconds is not null) return false;
                        if (!TryParseFilters(text, ref i, 1, 0, 59, 2, out filters)) return false;
                        interval.Seconds = filters;
                        interval.FilterLevel = Level.Second;
                        break;
                    case 'w':
                        if (At(text, i + 1) != 'd' || Level.Day < interval.FilterLevel || interval.WeekDays is not null) return false;
                        if (!TryParseFilters(text, ref i, 2, 1, 7, 1, out filters)) return false;
                        interval.WeekDays = filters;
                        interval.FilterLevel = Level.Day;
                        break;
                    case 'm' when At(text, i + 1) == 'd':
                        if (Level.Day < interval.FilterLevel || interval.WeekDays is not null || interval.MonthDays is not null) return false;
                        if (!TryParseFilters(text, ref i, 2, 1, 31, 2, out filters)) return false;
                        interval.MonthDays = filters;
                        interval.FilterLevel = Level.Day;
                        break;
                    case 'm':
                        if (Level.Minute < interval.FilterLevel || interval.Minutes is not null) return false;
                        if (!TryParseFilters(text, ref i, 1, 0, 59, 2, out filters)) return false;
                        interval.Minutes = filters;
                        interval.FilterLevel = Level.Minute;
                        break;
                    default:
                        return false;
                }
            }
            result = interval;
            return true;
        }

        /// <summary>The character at <paramref name="index"/>, or NUL past the end: what the C reads there (a <c>;</c> or the terminator).</summary>
        private static char At(ReadOnlySpan<char> text, int index) => index < text.Length ? text[index] : '\0';

        /// <summary><c>scheduler_parse_filter_r</c>: comma-separated <c>from[-to][/step]</c> after a <paramref name="prefix"/>-long unit.</summary>
        private static bool TryParseFilters(ReadOnlySpan<char> text, ref int i, int prefix, int min, int max, int width, [NotNullWhen(true)] out Filter[]? filters)
        {
            filters = null;
            var parsed = new List<Filter>();
            i += prefix;
            while (true)
            {
                int filterStart = i;
                int start, end, step = 1;
                int digits = i;
                while (digits < text.Length && char.IsAsciiDigit(text[digits])) digits++;
                if (digits != i)
                {
                    if (digits - i > width || !TryParseRange(text[i..digits], min, max, out start)) return false;
                    i = digits;
                    if (At(text, i) == '-')
                    {
                        int from = ++i;
                        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                        if (i == from || i - from > width || !TryParseRange(text[from..i], min, max, out end)) return false;
                        if (end < start) return false;
                    }
                    else
                    {
                        if (At(text, i) == '/') return false;                       // a step needs a range
                        end = start;
                    }
                }
                else
                {
                    start = min;
                    end = max;
                }

                if (At(text, i) == '/')
                {
                    int from = ++i;
                    while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                    if (i == from || i - from > width || !TryParseRange(text[from..i], 1, end - start, out step)) return false;
                }
                else if (i == filterStart)
                {
                    return false;                                                   // neither a value nor a step
                }

                parsed.Add(new Filter(start, end, step));
                if (At(text, i) != ',') break;
                if (++i >= text.Length) return false;                              // no filter after the comma
            }
            filters = [.. parsed];
            return true;
        }
    }

    /// <summary>
    /// The local clock of a zone, in the terms the C uses: <c>localtime</c> gives the broken-down time and its
    /// <c>tm_isdst</c>; <c>mktime</c> takes one back to an instant, resolving a skipped or repeated local time by the
    /// <c>tm_isdst</c> hint as glibc does. Instants are whole Unix seconds.
    /// </summary>
    private readonly struct Clock(TimeZoneInfo zone)
    {
        // glibc mktime: when the hint disagrees, look for a neighbouring instant with the hinted DST state, in steps of
        // the shortest DST period in the tz database, within half the longest (mktime.c, "Heuristic: probe the adjacent
        // timestamps in both directions").
        private const long Stride = 601200;
        private const long DeltaBound = (457243200 / 2) + Stride;

        public LocalTime Local(long t)
        {
            DateTimeOffset utc = DateTimeOffset.FromUnixTimeSeconds(t);
            DateTimeOffset local = TimeZoneInfo.ConvertTime(utc, zone);
            return new LocalTime(local.DateTime, zone.IsDaylightSavingTime(utc));
        }

        private bool IsDst(long t) => zone.IsDaylightSavingTime(DateTimeOffset.FromUnixTimeSeconds(t));

        private long Offset(long t) => (long)zone.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(t)).TotalSeconds;

        public long MakeTime(DateTime wall, bool isDst)
        {
            long wallSeconds = new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), TimeSpan.Zero).ToUnixTimeSeconds();

            // Every instant that reads as this wall time: an offset in force within two days of it, kept when the
            // round trip holds. None is a skipped time (a DST gap, or Apia's lost 30 December 2011), two a repeated one.
            // The round trip, not TimeZoneInfo.IsInvalidTime, decides: .NET calls Apia's lost day valid.
            var interpretations = new List<long>(2);
            for (int day = -2; day <= 2; day++)
            {
                long t = wallSeconds - Offset(wallSeconds + (day * SecondsPerDay));
                if (!interpretations.Contains(t) && Local(t).Wall == wall) interpretations.Add(t);
            }
            interpretations.Sort();                                                 // the earlier instant first
            foreach (long t in interpretations)
                if (IsDst(t) == isDst) return t;
            long guess = interpretations.Count > 0 ? interpretations[0] : wallSeconds - Offset(wallSeconds);

            for (long delta = Stride; delta < DeltaBound; delta += Stride)
            {
                foreach (int direction in (ReadOnlySpan<int>)[-1, 1])
                {
                    long probe = guess + (direction * delta);
                    if (IsDst(probe) == isDst) return wallSeconds - Offset(probe);
                }
            }
            return guess + (3600 * (isDst ? -1 : 1));                                 // "assume one-hour DST"
        }

        /// <summary><c>scheduler_find_dst_change</c>: the first minute in (start, end] whose DST state differs from start's.</summary>
        private long FindDstChange(long timeStart, long timeEnd)
        {
            long start = timeStart / 60;
            long end = timeEnd / 60;
            bool dstStart = IsDst(timeStart);
            while (end > start + 1)
            {
                long mid = (start + end) / 2;
                if (IsDst(mid * 60) == dstStart) start = mid;
                else end = mid;
            }
            return end * 60;
        }

        /// <summary><c>scheduler_get_nextcheck</c>: the earliest instant after <paramref name="now"/> any interval admits.</summary>
        public long ScheduledNextCheck(Scheduling[] intervals, long now)
        {
            LocalTime start = Local(now);
            long nextcheck = 0;
            foreach (Scheduling interval in intervals)
            {
                var tm = new BrokenDown(start.Wall);
                tm.Increment();
                tm.ApplyDay(interval);
                tm.ApplyHour(interval);
                tm.ApplyMinute(interval);
                tm.ApplySecond(interval);
                long current = MakeTime(tm.ToDateTime(), start.IsDst);

                LocalTime resolved = Local(current);
                if (resolved.IsDst != start.IsDst)
                {
                    // The instant crossed a DST change: re-apply the filters from the change itself.
                    LocalTime change = Local(FindDstChange(now, current));
                    var dst = new BrokenDown(change.Wall);
                    dst.ApplyDay(interval);
                    dst.ApplyHour(interval);
                    dst.ApplyMinute(interval);
                    dst.ApplySecond(interval);
                    current = MakeTime(dst.ToDateTime(), resolved.IsDst);
                }
                if (nextcheck == 0 || current < nextcheck) nextcheck = current;
            }
            return nextcheck;
        }
    }

    /// <summary>A local wall time and whether DST is in force there.</summary>
    private readonly record struct LocalTime(DateTime Wall, bool IsDst)
    {
        /// <summary>1 = Monday … 7 = Sunday, as <c>check_time_period</c> numbers days.</summary>
        public int IsoDayOfWeek => Wall.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)Wall.DayOfWeek;

        public int SecondOfDay => (int)Wall.TimeOfDay.TotalSeconds;
    }

    /// <summary>A mutable <c>struct tm</c> (month 0–11) and the filter steps that walk it forward.</summary>
    private struct BrokenDown(DateTime wall)
    {
        private int _year = wall.Year;
        private int _mon = wall.Month - 1;
        private int _mday = wall.Day;
        private int _hour = wall.Hour;
        private int _min = wall.Minute;
        private int _sec = wall.Second;

        public readonly DateTime ToDateTime()
            => new DateTime(_year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(_mon).AddDays(_mday - 1).AddHours(_hour).AddMinutes(_min).AddSeconds(_sec);

        /// <summary><c>scheduler_tm_inc</c>: one second on.</summary>
        public void Increment()
        {
            if (++_sec < 60) return;
            _sec = 0;
            if (++_min < 60) return;
            _min = 0;
            if (++_hour < 24) return;
            _hour = 0;
            if (DaysInMonth(_year, _mon + 1) >= ++_mday) return;
            _mday = 1;
            if (++_mon < 12) return;
            _mon = 0;
            _year++;
        }

        public void ApplyDay(Scheduling interval)
        {
            (int day, int mon, int year) = (_mday, _mon, _year);
            while (!DayNextCheck(interval))
            {
                if (++_mon > 11)
                {
                    _mon = 0;
                    _year++;
                }
                _mday = 1;
            }
            if (_mday != day || _mon != mon || _year != year) (_hour, _min, _sec) = (0, 0, 0);
        }

        public void ApplyHour(Scheduling interval)
        {
            int hour = _hour;
            while (!FilterNextCheck(interval, Level.Hour))
            {
                _mday++;
                _hour = 0;
                ApplyDay(interval);                                                 // the day changed: re-apply its filter
            }
            if (_hour != hour) (_min, _sec) = (0, 0);
        }

        public void ApplyMinute(Scheduling interval)
        {
            int min = _min;
            while (!FilterNextCheck(interval, Level.Minute))
            {
                _hour++;
                _min = 0;
                ApplyHour(interval);
            }
            if (_min != min) _sec = 0;
        }

        public void ApplySecond(Scheduling interval)
        {
            while (!FilterNextCheck(interval, Level.Second))
            {
                _min++;
                _sec = 0;
                ApplyMinute(interval);
            }
        }

        /// <summary><c>scheduler_get_filter_nextcheck</c> for the hour, minute and second levels.</summary>
        private bool FilterNextCheck(Scheduling interval, Level level)
        {
            (Filter[]? filters, int max) = level switch
            {
                Level.Hour => (interval.Hours, 23),
                Level.Minute => (interval.Minutes, 59),
                _ => (interval.Seconds, 59),
            };
            ref int value = ref level == Level.Hour ? ref _hour : ref level == Level.Minute ? ref _min : ref _sec;
            if (value > max) return false;
            if (filters is null)
            {
                // An unspecified unit finer than the interval's own level matches 0 only (m30: second 0); a coarser
                // one matches everything (m30: every hour).
                return interval.FilterLevel > level || value == 0;
            }
            return NearestFilterValue(filters, ref value);
        }

        /// <summary><c>scheduler_get_day_nextcheck</c>: the next day the month-day and week-day filters admit, in this month.</summary>
        private bool DayNextCheck(Scheduling interval)
        {
            if (_year < 1970 || _mday < 1 || _mday > DaysInMonth(_year, _mon + 1) || _hour > 23 || _min > 59 || _sec > 61) return false;
            if (interval.MonthDays is null) return WeekDayNextCheck(interval);
            while (NearestFilterValue(interval.MonthDays, ref _mday))
            {
                if (_mday > DaysInMonth(_year, _mon + 1)) break;
                if (MatchesWeekDay(interval)) return true;
                _mday++;
                if (_mday > DaysInMonth(_year, _mon + 1)) break;
            }
            return false;
        }

        private bool WeekDayNextCheck(Scheduling interval)
        {
            if (interval.WeekDays is null) return true;
            int now = DayOfWeek(_year, _mon + 1, _mday);
            int next = now;
            if (!NearestFilterValue(interval.WeekDays, ref next))
            {
                _mday += 7 - now + 1;                                               // on to next week's Monday
                now = next = 1;
                if (!NearestFilterValue(interval.WeekDays, ref next)) return false;  // a valid week-day filter always matches
            }
            _mday += next - now;
            return _mday <= DaysInMonth(_year, _mon + 1);
        }

        private readonly bool MatchesWeekDay(Scheduling interval)
        {
            if (interval.WeekDays is null) return true;
            int value = DayOfWeek(_year, _mon + 1, _mday);
            foreach (Filter filter in interval.WeekDays)
            {
                if (filter.Start <= value && value <= filter.End)
                {
                    int next = value;
                    int offset = (next - filter.Start) % filter.Step;
                    if (offset != 0) next += filter.Step - offset;
                    if (next <= filter.End) return true;
                }
            }
            return false;
        }

        /// <summary><c>scheduler_get_nearest_filter_value</c>: the smallest admitted value at or after <paramref name="value"/>.</summary>
        private static bool NearestFilterValue(Filter[] filters, ref int value)
        {
            Filter? nearest = null;
            foreach (Filter filter in filters)
            {
                if (filter.Start <= value && value <= filter.End)
                {
                    int next = value;
                    int offset = (next - filter.Start) % filter.Step;
                    if (offset != 0) next += filter.Step - offset;
                    if (next <= filter.End)
                    {
                        value = next;
                        return true;
                    }
                }
                if (filter.Start > value && (nearest is null || nearest.Value.Start > filter.Start)) nearest = filter;
            }
            if (nearest is null) return false;
            value = nearest.Value.Start;
            return true;
        }

        /// <summary><c>calculate_dayofweek</c>: 1 = Monday … 7 = Sunday.</summary>
        private static int DayOfWeek(int year, int mon, int mday)
        {
            ReadOnlySpan<int> table = [0, 3, 2, 5, 0, 3, 5, 1, 4, 6, 2, 4];
            if (mon < 3) year--;
            return ((year + (year / 4) - (year / 100) + (year / 400) + table[mon - 1] + mday - 1) % 7) + 1;
        }

        /// <summary><c>zbx_day_in_month</c>: 30 for a month out of range, as upstream answers.</summary>
        private static int DaysInMonth(int year, int mon) => mon is >= 1 and <= 12 ? DateTime.DaysInMonth(year, mon) : 30;
    }
}
