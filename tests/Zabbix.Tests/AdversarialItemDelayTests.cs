// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;

namespace Community.Zabbix.Tests;

/// <summary>
/// Adversarial tests of the active agent's schedule: the manual's own examples of flexible and scheduling intervals
/// (<c>50s</c>, <c>1m/1-7,00:00-24:00</c> beside <c>1s/1-5,09:00-18:00</c>, <c>h9-18/2</c>, <c>wd1-5h9-18</c>,
/// <c>md1-31h0m0</c>) walked across a week from fixed instants, and delays the server must never be able to hand an
/// agent (<c>-1</c>, <c>1y</c>, a flexible interval with no update interval before it).
/// </summary>
public sealed class AdversarialItemDelayTests
{
    // 21 September 2026 is a Monday.
    private static DateTimeOffset At(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    private static DateTimeOffset Next(string delay, string from, long seed = 0)
        => ZabbixItemDelay.Parse(delay, CultureInfo.InvariantCulture).GetNextCheck(At(from), TimeZoneInfo.Utc, seed) ?? throw new InvalidOperationException("never due");

    [Theory]
    [InlineData("2026-09-21 10:00:00 +00:00", "2026-09-21 10:00:50 +00:00")]                      // 10:00:00 is a multiple of 50 s: due strictly after it
    [InlineData("2026-09-21 10:00:49 +00:00", "2026-09-21 10:00:50 +00:00")]
    [InlineData("2026-09-21 10:00:50 +00:00", "2026-09-21 10:01:40 +00:00")]
    public void Fifty_seconds_falls_due_on_every_multiple_of_fifty_seconds_since_the_epoch(string from, string due)
        => Assert.Equal(At(due), Next("50s", from));

    [Fact]
    public void A_seed_phases_the_cadence_without_changing_it()
    {
        DateTimeOffset first = Next("50s", "2026-09-21 10:00:00 +00:00", seed: 7);
        DateTimeOffset second = ZabbixItemDelay.Parse("50s", CultureInfo.InvariantCulture).GetNextCheck(first, TimeZoneInfo.Utc, 7)!.Value;

        Assert.Equal(TimeSpan.FromSeconds(50), second - first);
        Assert.Equal(7, first.ToUnixTimeSeconds() % 50);
    }

    [Theory]
    [InlineData("2026-09-23 10:00:00 +00:00", "2026-09-23 10:00:01 +00:00")]                      // a Wednesday in office hours: the 1 s interval wins
    [InlineData("2026-09-23 18:00:00 +00:00", "2026-09-23 18:01:00 +00:00")]                      // office hours end at 18:00: back to a minute
    [InlineData("2026-09-26 10:00:00 +00:00", "2026-09-26 10:01:00 +00:00")]                      // a Saturday: only the every-day minute applies
    [InlineData("2026-09-23 08:59:30 +00:00", "2026-09-23 09:00:00 +00:00")]                      // the 1 s interval starts at 09:00, not at the next minute
    public void Of_two_overlapping_flexible_intervals_the_shorter_delay_applies_while_both_last(string from, string due)
        => Assert.Equal(At(due), Next("0;1m/1-7,00:00-24:00;1s/1-5,09:00-18:00", from));

    [Theory]
    [InlineData("2026-09-21 08:30:00 +00:00", "2026-09-21 09:00:00 +00:00")]
    [InlineData("2026-09-21 09:00:00 +00:00", "2026-09-21 11:00:00 +00:00")]
    [InlineData("2026-09-21 16:59:59 +00:00", "2026-09-21 17:00:00 +00:00")]
    [InlineData("2026-09-21 17:00:00 +00:00", "2026-09-22 09:00:00 +00:00")]                      // 19:00 is past 18: tomorrow at 09:00
    public void Every_second_hour_from_nine_to_eighteen_falls_due_at_nine_eleven_thirteen_fifteen_and_seventeen(string from, string due)
        => Assert.Equal(At(due), Next("0;h9-18/2", from));

    [Theory]
    [InlineData("2026-09-25 17:30:00 +00:00", "2026-09-25 18:00:00 +00:00")]                      // a Friday: 18:00 is still in
    [InlineData("2026-09-25 18:00:00 +00:00", "2026-09-28 09:00:00 +00:00")]                      // after Friday 18:00 comes Monday 09:00
    [InlineData("2026-09-26 12:00:00 +00:00", "2026-09-28 09:00:00 +00:00")]                      // a Saturday
    public void Every_hour_from_nine_to_eighteen_on_working_days_skips_the_weekend(string from, string due)
        => Assert.Equal(At(due), Next("0;wd1-5h9-18", from));

    [Theory]
    [InlineData("2026-09-21 12:00:00 +00:00", "2026-09-22 00:00:00 +00:00")]
    [InlineData("2026-09-30 23:59:59 +00:00", "2026-10-01 00:00:00 +00:00")]                      // across the month's end
    [InlineData("2026-12-31 00:00:00 +00:00", "2027-01-01 00:00:00 +00:00")]                      // across the year's end
    public void Every_day_of_the_month_at_midnight_falls_due_at_the_next_midnight(string from, string due)
        => Assert.Equal(At(due), Next("0;md1-31h0m0", from));

    [Fact]
    public void A_scheduling_interval_beside_a_cadence_adds_its_instants_to_it()
    {
        // A 1 h cadence from 08:30 is next at 09:00 (a multiple of an hour); the scheduling interval at 08:45 comes first.
        Assert.Equal(At("2026-09-21 08:45:00 +00:00"), Next("1h;h8m45", "2026-09-21 08:30:00 +00:00"));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1y")]
    [InlineData("0")]
    [InlineData("1m/1-7,00:00-24:00;1s/1-5,09:00-18:00")]                                         // a flexible interval is not an update interval
    [InlineData("1s/1-5,09:00-18:00")]
    [InlineData("h9-18/2")]
    [InlineData("0;h25")]
    [InlineData("0;wd8")]
    [InlineData("0;md32")]
    [InlineData("9223372036854775807")]
    public void A_delay_no_upstream_agent_accepts_is_refused_with_a_reason(string delay)
    {
        bool parsed = ZabbixItemDelay.TryParse(delay, out _, out string? error);

        Assert.False(parsed);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void A_delay_that_is_zero_in_every_interval_is_never_due()
    {
        ZabbixItemDelay delay = ZabbixItemDelay.Parse("0;1/1-7,00:00-00:01", CultureInfo.InvariantCulture);

        DateTimeOffset? next = delay.GetNextCheck(At("2026-09-21 12:00:00 +00:00"), TimeZoneInfo.Utc);

        Assert.Equal(At("2026-09-22 00:00:00 +00:00"), next);                                     // only the first minute of each day polls
    }
}
