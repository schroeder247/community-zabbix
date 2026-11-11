// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace Community.Zabbix;

/// <summary>
/// Represents Zabbix's instant, <c>clock</c> and <c>ns</c>: whole seconds since the Unix epoch and the nanoseconds within
/// the second, exactly as history rows, sender frames and proxy uploads carry it. History's primary key is
/// <c>(itemid, clock, ns)</c>, so the instant must round-trip to the nanosecond.
/// </summary>
/// <remarks>
/// <see cref="DateTimeOffset"/> has 100-nanosecond ticks, so <c>ns = 123456789</c> comes back as <c>123456700</c> and two
/// values 50 ns apart collide on the history key. <see cref="DateTime"/> has the same resolution, and
/// <see cref="TimeSpan"/> is a duration, not an instant. So the domain keeps its own pair, converts <i>from</i>
/// <see cref="DateTimeOffset"/> implicitly (lossless: a tick is 100 ns), and <i>to</i> it only through the named, lossy
/// <see cref="ToDateTimeOffset"/>. DateTimeOffset cannot be extended to a finer unit, so the SDK owns the pair and
/// implements only comparison, span parsing and formatting.
/// </remarks>
internal readonly record struct ZabbixClock : IComparable<ZabbixClock>, IComparable, IComparisonOperators<ZabbixClock, ZabbixClock, bool>, IAdditionOperators<ZabbixClock, TimeSpan, ZabbixClock>, ISubtractionOperators<ZabbixClock, TimeSpan, ZabbixClock>, ISubtractionOperators<ZabbixClock, ZabbixClock, TimeSpan>, ISpanFormattable, ISpanParsable<ZabbixClock>
{
    private const int NanosecondsPerSecond = 1_000_000_000;
    private const int NanosecondsPerTick = 100;

    /// <summary>An instant from Zabbix's two fields.</summary>
    /// <param name="seconds">Seconds since the Unix epoch (<c>clock</c>).</param>
    /// <param name="nanoseconds">Nanoseconds within the second (<c>ns</c>), 0 to 999,999,999.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nanoseconds"/> is outside the second.</exception>
    public ZabbixClock(long seconds, int nanoseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nanoseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(nanoseconds, NanosecondsPerSecond);
        Seconds = seconds;
        Nanoseconds = nanoseconds;
    }

    /// <summary>The Unix epoch, <c>clock = 0, ns = 0</c>.</summary>
    public static ZabbixClock UnixEpoch => default;

    /// <summary>Seconds since the Unix epoch: Zabbix's <c>clock</c>.</summary>
    public long Seconds { get; }

    /// <summary>Nanoseconds within the second: Zabbix's <c>ns</c>.</summary>
    public int Nanoseconds { get; }

    /// <summary>The instant <paramref name="time"/> reads now: a value's clock taken on the host's injected clock.</summary>
    /// <param name="time">The clock.</param>
    /// <returns>Now, to the 100 ns the clock carries.</returns>
    public static ZabbixClock Now(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return FromDateTimeOffset(time.GetUtcNow());
    }

    /// <summary>The instant <paramref name="time"/> names; lossless, since a tick is 100 ns.</summary>
    /// <param name="time">The instant.</param>
    /// <returns>The clock.</returns>
    public static ZabbixClock FromDateTimeOffset(DateTimeOffset time)
    {
        long ticks = time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        long seconds = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out long remainder);
        if (remainder < 0) { seconds--; remainder += TimeSpan.TicksPerSecond; }
        return new ZabbixClock(seconds, (int)(remainder * NanosecondsPerTick));
    }

    /// <summary>The instant as a <see cref="DateTimeOffset"/> in UTC, truncated to its 100-ns tick.</summary>
    /// <returns>The instant.</returns>
    public DateTimeOffset ToDateTimeOffset()
        => DateTimeOffset.UnixEpoch.AddTicks((Seconds * TimeSpan.TicksPerSecond) + (Nanoseconds / NanosecondsPerTick));

    /// <summary>Nanoseconds since the Unix epoch, for a single ordered column (valid until the year 2262).</summary>
    /// <returns>The count.</returns>
    public long ToUnixNanoseconds() => checked((Seconds * NanosecondsPerSecond) + Nanoseconds);

    /// <summary>The instant <paramref name="nanoseconds"/> after the Unix epoch.</summary>
    /// <param name="nanoseconds">Nanoseconds since the epoch.</param>
    /// <returns>The clock.</returns>
    public static ZabbixClock FromUnixNanoseconds(long nanoseconds)
    {
        long seconds = Math.DivRem(nanoseconds, NanosecondsPerSecond, out long remainder);
        if (remainder < 0) { seconds--; remainder += NanosecondsPerSecond; }
        return new ZabbixClock(seconds, (int)remainder);
    }

    /// <summary>The instant <paramref name="offset"/> after <paramref name="clock"/>, as <see cref="DateTimeOffset"/> adds a <see cref="TimeSpan"/>; a 100-ns tick is the finest step.</summary>
    /// <param name="clock">The instant.</param>
    /// <param name="offset">How much later (negative: earlier).</param>
    /// <returns>The shifted instant.</returns>
    public static ZabbixClock operator +(ZabbixClock clock, TimeSpan offset) => FromUnixNanoseconds(checked(clock.ToUnixNanoseconds() + (offset.Ticks * NanosecondsPerTick)));

    /// <summary>The instant <paramref name="offset"/> before <paramref name="clock"/>.</summary>
    /// <param name="clock">The instant.</param>
    /// <param name="offset">How much earlier (negative: later).</param>
    /// <returns>The shifted instant.</returns>
    public static ZabbixClock operator -(ZabbixClock clock, TimeSpan offset) => FromUnixNanoseconds(checked(clock.ToUnixNanoseconds() - (offset.Ticks * NanosecondsPerTick)));

    /// <summary>The time from <paramref name="right"/> to <paramref name="left"/>, truncated to its 100-ns tick; negative when <paramref name="left"/> is the earlier.</summary>
    /// <param name="left">The later instant.</param>
    /// <param name="right">The earlier instant.</param>
    /// <returns>The difference.</returns>
    public static TimeSpan operator -(ZabbixClock left, ZabbixClock right) => TimeSpan.FromTicks(checked(left.ToUnixNanoseconds() - right.ToUnixNanoseconds()) / NanosecondsPerTick);

    /// <summary>The instant <paramref name="offset"/> after this one; the named form of <c>+</c>.</summary>
    /// <param name="offset">How much later (negative: earlier).</param>
    /// <returns>The shifted instant.</returns>
    public ZabbixClock Add(TimeSpan offset) => this + offset;

    /// <summary>The instant <paramref name="offset"/> before this one; the named form of <c>-</c>.</summary>
    /// <param name="offset">How much earlier (negative: later).</param>
    /// <returns>The shifted instant.</returns>
    public ZabbixClock Subtract(TimeSpan offset) => this - offset;

    /// <summary>The time from <paramref name="other"/> to this instant; the named form of <c>-</c>.</summary>
    /// <param name="other">The earlier instant.</param>
    /// <returns>The difference.</returns>
    public TimeSpan Subtract(ZabbixClock other) => this - other;

    /// <summary>An instant from a <see cref="DateTimeOffset"/>; lossless.</summary>
    /// <param name="time">The instant.</param>
    /// <returns>The clock of the same instant, to the 100 ns the value carries.</returns>
    public static implicit operator ZabbixClock(DateTimeOffset time) => FromDateTimeOffset(time);

    /// <inheritdoc/>
    public int CompareTo(ZabbixClock other) => Seconds != other.Seconds ? Seconds.CompareTo(other.Seconds) : Nanoseconds.CompareTo(other.Nanoseconds);

    /// <inheritdoc/>
    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        ZabbixClock other => CompareTo(other),
        _ => throw new ArgumentException($"The object is not a {nameof(ZabbixClock)}.", nameof(obj)),
    };

    /// <inheritdoc/>
    public static bool operator <(ZabbixClock left, ZabbixClock right) => left.CompareTo(right) < 0;

    /// <inheritdoc/>
    public static bool operator >(ZabbixClock left, ZabbixClock right) => left.CompareTo(right) > 0;

    /// <inheritdoc/>
    public static bool operator <=(ZabbixClock left, ZabbixClock right) => left.CompareTo(right) <= 0;

    /// <inheritdoc/>
    public static bool operator >=(ZabbixClock left, ZabbixClock right) => left.CompareTo(right) >= 0;

    /// <summary>The instant as <c>clock.ns</c> with nine digits of nanoseconds (<c>1790169446.950545304</c>).</summary>
    /// <returns>The text.</returns>
    public override string ToString() => ToString(null, CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        ZabbixFormat.ThrowIfNotGeneral(format, nameof(ZabbixClock));
        return string.Create(CultureInfo.InvariantCulture, $"{Seconds}.{Nanoseconds:D9}");
    }

    /// <inheritdoc/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        ZabbixFormat.ThrowIfNotGeneral(format, nameof(ZabbixClock));
        return destination.TryWrite(CultureInfo.InvariantCulture, $"{Seconds}.{Nanoseconds:D9}", out charsWritten);
    }

    /// <inheritdoc/>
    public static ZabbixClock Parse(string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan(), provider);
    }

    /// <inheritdoc/>
    public static ZabbixClock Parse(ReadOnlySpan<char> s, IFormatProvider? provider)
        => TryParse(s, provider, out ZabbixClock clock) ? clock : throw new FormatException($"'{s}' is not a clock in the form seconds.nanoseconds.");

    /// <inheritdoc/>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ZabbixClock result)
        => TryParse(s.AsSpan(), provider, out result);

    /// <summary>Parses <c>clock</c> or <c>clock.ns</c> (the nanoseconds padded or not to nine digits, as Zabbix writes them).</summary>
    /// <param name="s">The text.</param>
    /// <param name="provider">Ignored: the form is invariant.</param>
    /// <param name="result">The clock.</param>
    /// <returns>Whether the text was an instant.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out ZabbixClock result)
    {
        result = default;
        int dot = s.IndexOf('.');
        ReadOnlySpan<char> seconds = dot < 0 ? s : s[..dot];
        if (!long.TryParse(seconds, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long clock)) return false;
        int ns = 0;
        if (dot >= 0)
        {
            ReadOnlySpan<char> fraction = s[(dot + 1)..];
            if (fraction.IsEmpty || fraction.Length > 9 || !int.TryParse(fraction, NumberStyles.None, CultureInfo.InvariantCulture, out ns)) return false;
            for (int i = fraction.Length; i < 9; i++) ns *= 10;
        }
        result = new ZabbixClock(clock, ns);
        return true;
    }
}
