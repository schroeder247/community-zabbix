// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;

namespace Community.Zabbix.Tests;

/// <summary>Zabbix's instant is <c>(clock, ns)</c>; history's key includes <c>ns</c>, so nothing may round it to a tick.</summary>
public sealed class ZabbixClockTests
{
    [Fact]
    public void Two_values_fifty_nanoseconds_apart_stay_distinct_and_ordered()
    {
        var first = new ZabbixClock(1_700_000_000, 123_456_700);
        var second = new ZabbixClock(1_700_000_000, 123_456_750);

        Assert.NotEqual(first, second);
        Assert.True(first < second);
        Assert.Equal(first.ToDateTimeOffset(), second.ToDateTimeOffset());          // why DateTimeOffset is not the type
    }

    [Theory]
    [InlineData(1_700_000_000L, 0)]
    [InlineData(1_700_000_000L, 999_999_999)]
    [InlineData(0L, 1)]
    [InlineData(-1L, 999_999_999)]
    public void Nanoseconds_since_the_epoch_round_trip_exactly(long seconds, int nanoseconds)
    {
        var clock = new ZabbixClock(seconds, nanoseconds);

        Assert.Equal(clock, ZabbixClock.FromUnixNanoseconds(clock.ToUnixNanoseconds()));
    }

    [Fact]
    public void A_DateTimeOffset_converts_losslessly_in_any_offset()
    {
        DateTimeOffset local = new DateTimeOffset(2026, 9, 23, 14, 0, 0, TimeSpan.FromHours(2)).AddTicks(1_234_567);

        ZabbixClock clock = local;

        Assert.Equal(local.ToUnixTimeSeconds(), clock.Seconds);
        Assert.Equal(123_456_700, clock.Nanoseconds);
        Assert.Equal(local, clock.ToDateTimeOffset());
    }

    [Fact]
    public void An_instant_before_the_epoch_keeps_nanoseconds_within_the_second()
    {
        ZabbixClock clock = DateTimeOffset.UnixEpoch.AddTicks(-1);

        Assert.Equal(new ZabbixClock(-1, 999_999_900), clock);
    }

    [Theory]
    [InlineData("1700000000.000000501", 1_700_000_000L, 501)]
    [InlineData("1700000000.5", 1_700_000_000L, 500_000_000)]
    [InlineData("1700000000", 1_700_000_000L, 0)]
    public void Text_is_clock_dot_ns(string text, long seconds, int nanoseconds)
    {
        ZabbixClock clock = ZabbixClock.Parse(text, CultureInfo.InvariantCulture);

        Assert.Equal(new ZabbixClock(seconds, nanoseconds), clock);
        Assert.Equal(clock, ZabbixClock.Parse(clock.ToString(), CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1700000000.")]
    [InlineData("1700000000.1234567890")]
    [InlineData("1700000000.-1")]
    [InlineData("abc")]
    public void Anything_else_is_not_an_instant(string text)
        => Assert.False(ZabbixClock.TryParse(text, CultureInfo.InvariantCulture, out _));

    [Fact]
    public void Nanoseconds_outside_the_second_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZabbixClock(0, 1_000_000_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZabbixClock(0, -1));
    }

    [Fact]
    public void A_time_span_shifts_a_clock_and_the_difference_of_two_clocks_is_the_span()
    {
        // Maintenance windows, deadlines and ages are clock arithmetic: the same shape DateTimeOffset gives a TimeSpan.
        var start = new ZabbixClock(1_791_589_500, 999_999_900);

        ZabbixClock later = start + TimeSpan.FromHours(2);
        ZabbixClock back = later - TimeSpan.FromHours(2);

        Assert.Equal(new ZabbixClock(1_791_596_700, 999_999_900), later);
        Assert.Equal(start, back);
        Assert.Equal(TimeSpan.FromHours(2), later - start);
        Assert.Equal(-TimeSpan.FromHours(2), start - later);
        Assert.Equal(later, start.Add(TimeSpan.FromHours(2)));
        Assert.Equal(start, later.Subtract(TimeSpan.FromHours(2)));
        Assert.Equal(TimeSpan.FromHours(2), later.Subtract(start));
    }

    [Fact]
    public void A_shift_carries_across_the_second_and_keeps_the_nanoseconds_a_tick_cannot_see()
    {
        // 100 ns is the finest step a TimeSpan has; a clock's own nanoseconds below a tick stay as they were.
        var clock = new ZabbixClock(10, 999_999_950);

        ZabbixClock shifted = clock + TimeSpan.FromTicks(1);

        Assert.Equal(new ZabbixClock(11, 50), shifted);
        Assert.Equal(TimeSpan.Zero, new ZabbixClock(10, 60) - new ZabbixClock(10, 10));
    }
}
