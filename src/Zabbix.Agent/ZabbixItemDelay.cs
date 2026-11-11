// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using Filter = Community.Zabbix.ZabbixIntervalGrammar.Filter;
using Level = Community.Zabbix.ZabbixIntervalGrammar.Level;
using Period = Community.Zabbix.ZabbixIntervalGrammar.Period;
using Schedule = Community.Zabbix.ZabbixIntervalGrammar.Schedule;

namespace Community.Zabbix;

/// <summary>
/// Schedules an item by its update interval, as an agent does: <c>30s</c> is a plain update interval;
/// <c>50s;10s/1-5,09:00-18:00</c> adds a flexible interval (every 10 s on weekdays in office hours); <c>0;wd1-5h9m30</c>
/// is a scheduling interval (weekdays at 09:30, and nothing else). <see cref="GetNextCheck(DateTimeOffset, TimeZoneInfo, long)"/>
/// answers when the item is due. The public face is <see cref="ZabbixInterval"/> and its <c>GetNextCheck</c>
/// (<see cref="ZabbixIntervalExtensions"/>); this is what the agent's due queue holds once it has read an interval.
/// </summary>
/// <remarks>
/// <para>The grammar is <see cref="ZabbixInterval"/>'s, read once (<see cref="ZabbixIntervalGrammar"/>); an agent adds
/// upstream's own rules and the schedule, which mirror Zabbix's C (<c>src/libs/zbxexpr/interval.c</c>, identical in 7.0,
/// 7.4 and 8.0), because an agent that disagrees collects at instants the server does not expect; the golden tests are
/// upstream's <c>tests/libs/zbxexpr/*.yaml</c>. User macros are resolved by the server before an agent sees the delay, so
/// one an agent sees is refused.</para>
/// <para>No BCL type parses or schedules this grammar, and the cron libraries cannot express a flexible interval and
/// resolve a daylight-saving gap or overlap differently from Zabbix's <c>mktime</c> <c>tm_isdst</c> hint. Only the
/// next-check arithmetic is owned here; time zones, calendars and the clock are the BCL's.</para>
/// </remarks>
internal sealed class ZabbixItemDelay : IEquatable<ZabbixItemDelay>, ISpanParsable<ZabbixItemDelay>, ISpanFormattable
{
    private const int SecondsPerDay = 86400;
    private const long SecondsPerYear = 365L * SecondsPerDay;

    private readonly Flexible[] _flexible;
    private readonly Schedule[] _scheduling;

    private ZabbixItemDelay(string text, int updateInterval, Flexible[] flexible, Schedule[] scheduling)
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
    /// Parses <paramref name="s"/> and, when it is not a delay an agent schedules, says why in upstream's words
    /// (<c>Invalid update interval "…".</c>, <c>Invalid flexible interval "…".</c>, <c>Invalid scheduling interval "…".</c>),
    /// which is what an agent reports as the item's not-supported reason. The grammar is <see cref="ZabbixInterval"/>'s;
    /// an agent adds upstream's rules (<c>zbx_interval_preproc</c>): no macro, an update interval of at most a day and of 0
    /// only beside a custom interval, and a flexible interval of 0 only beside an update interval that isn't.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="result">The delay.</param>
    /// <param name="error">Why it is not one.</param>
    /// <returns>Whether it parsed.</returns>
    internal static bool TryParse(ReadOnlySpan<char> s, [MaybeNullWhen(false)] out ZabbixItemDelay result, [NotNullWhen(false)] out string? error)
    {
        result = null;
        if (!ZabbixIntervalGrammar.TryParse(s, out ZabbixIntervalGrammar.Parts? parts, out ZabbixIntervalGrammar.Failure failure, out string segment))
        {
            error = Invalid(failure switch { ZabbixIntervalGrammar.Failure.Update => "update", ZabbixIntervalGrammar.Failure.Flexible => "flexible", _ => "scheduling" }, segment);
            return false;
        }
        if (parts.UpdateSeconds is not { } simple)
        {
            error = Invalid("update", parts.Update);
            return false;
        }

        var flexible = new List<Flexible>();
        var scheduling = new List<Schedule>();
        foreach (ZabbixIntervalGrammar.Custom custom in parts.Customs)
        {
            if (custom.IsFlexible)
            {
                if (custom.Delay is not { } delay || custom.Period is not { } period || (simple == 0 && delay == 0))
                {
                    error = Invalid("flexible", custom.Text);
                    return false;
                }
                flexible.Add(new Flexible((int)delay, period));
            }
            else
            {
                if (custom.Schedule is not { } schedule)
                {
                    error = Invalid("scheduling", custom.Text);
                    return false;
                }
                scheduling.Add(schedule);
            }
        }

        if ((flexible.Count == 0 && scheduling.Count == 0 && simple == 0) || simple > SecondsPerDay)
        {
            error = Invalid("update", parts.Customs.Length > 0 ? parts.Customs[^1].Text : s);   // upstream names the last segment it read
            return false;
        }

        // Upstream prepends to its lists; the order only decides ties that give the same instant, so it is kept as read.
        result = new ZabbixItemDelay(s.ToString(), (int)simple, [.. flexible], [.. scheduling]);
        error = null;
        return true;
    }

    private static string Invalid(string kind, ReadOnlySpan<char> text) => $"Invalid {kind} interval \"{text}\".";

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
            if ((current == -1 || interval.Delay < current) && interval.Contains(local)) current = interval.Delay;
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

    /// <summary>A flexible interval: the delay in force over its period.</summary>
    private readonly record struct Flexible(int Delay, Period Period)
    {
        public bool Contains(LocalTime local)
            => Period.StartDay <= local.IsoDayOfWeek && local.IsoDayOfWeek <= Period.EndDay && Period.StartTime <= local.SecondOfDay && local.SecondOfDay < Period.EndTime;
    }

    // ----- scheduling intervals: scheduler_get_nextcheck -----------------------------------------------------------------

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
        public long ScheduledNextCheck(Schedule[] intervals, long now)
        {
            LocalTime start = Local(now);
            long nextcheck = 0;
            foreach (Schedule interval in intervals)
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

        public void ApplyDay(Schedule interval)
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

        public void ApplyHour(Schedule interval)
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

        public void ApplyMinute(Schedule interval)
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

        public void ApplySecond(Schedule interval)
        {
            while (!FilterNextCheck(interval, Level.Second))
            {
                _min++;
                _sec = 0;
                ApplyMinute(interval);
            }
        }

        /// <summary><c>scheduler_get_filter_nextcheck</c> for the hour, minute and second levels.</summary>
        private bool FilterNextCheck(Schedule interval, Level level)
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
                return interval.Level > level || value == 0;
            }
            return NearestFilterValue(filters, ref value);
        }

        /// <summary><c>scheduler_get_day_nextcheck</c>: the next day the month-day and week-day filters admit, in this month.</summary>
        private bool DayNextCheck(Schedule interval)
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

        private bool WeekDayNextCheck(Schedule interval)
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

        private readonly bool MatchesWeekDay(Schedule interval)
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
