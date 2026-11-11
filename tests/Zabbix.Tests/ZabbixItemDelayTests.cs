// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;

namespace Community.Zabbix.Tests;

/// <summary>
/// The delay grammar and its schedule, golden-tested from Zabbix's own test data (<c>tests/libs/zbxexpr/*.yaml</c> in
/// the 7.0/7.4/8.0 checkouts, identical in all three, as is <c>src/libs/zbxexpr/interval.c</c>). Each case names the
/// file it came from; the inputs and expected outputs are upstream's, transcribed.
/// </summary>
public sealed class ZabbixItemDelayTests
{
    // zbx_interval_preproc.yaml (the cases that parse custom intervals, which is what an agent does)
    [Theory]
    [InlineData("1", true)]
    [InlineData("86400", true)]
    [InlineData("86401", false)]
    [InlineData("1s", true)]
    [InlineData("86400s", true)]
    [InlineData("86401s", false)]
    [InlineData("1m", true)]
    [InlineData("1440m", true)]
    [InlineData("1441m", false)]
    [InlineData("1h", true)]
    [InlineData("24h", true)]
    [InlineData("25h", false)]
    [InlineData("1d", true)]
    [InlineData("2d", false)]
    [InlineData("1w", false)]
    [InlineData("0", false)]
    [InlineData("0s", false)]
    [InlineData("1;foo/bar,00:00-23:00;0/1-6,00:00-23:00;baz1-5h9-18;;;;", false)]
    [InlineData("0;0/1-6,00:00-23:00", false)]
    [InlineData("0;1/1-6,00:00-23:00;0/1-6,00:00-23:00", false)]
    [InlineData("0;0/1-6,00:00-23:00;1/1-6,00:00-23:00;wd1-5h9-18", false)]
    [InlineData("0;1/1-6,00:00-23:00", true)]
    [InlineData("1;0/1-6,00:00-23:00", true)]
    [InlineData("0;wd1-5h9-18", true)]
    [InlineData("0;", false)]
    // zbx_validate_interval.yaml, the cases without user macros (the server resolves those before an agent sees a delay)
    [InlineData("1/", false)]
    [InlineData("x", false)]
    [InlineData("1;", false)]
    [InlineData("1;;", false)]
    [InlineData("1s;2s/1-6,00:00-23:00", true)]
    [InlineData("1s;2s/1-6,00:00-23:00;0/1-6,00:00-23:00", true)]
    [InlineData("a;2s/1-6,00:00-23:00", false)]
    [InlineData("1;s/1-6,00:00-23:00", false)]
    [InlineData("1;1/c", false)]
    [InlineData("1;;0/1-6,00:00-23:00", false)]
    [InlineData("1;0//1-6,00:00-23:00", false)]
    [InlineData("1;0x1-6,00:00-23:00", false)]
    [InlineData("1s;2s/1-6,00:00-23:00;", false)]
    [InlineData("1s;1-6,00:00-23:00", false)]
    [InlineData("1s;2s", false)]
    [InlineData("1s;2s/", false)]
    [InlineData("0;2s/1-6,00:00-23:00", true)]
    [InlineData("0;2s/1-6,00:00-23:00;1/1-6,00:00-23:00", true)]
    [InlineData("1;0/1-6,00:00-23:00;0/1-6,00:00-23:00", true)]
    [InlineData("0;2s/1-6,00:00-23:00;0/1-6,00:00-23:00", false)]
    [InlineData("86400;2147483647/1-6,00:00-23:00", true)]
    [InlineData("86401;1/1-6,00:00-23:00", false)]
    [InlineData("1;9223372036854775807/1-6,00:00-23:00", false)]
    [InlineData("1;2147483648/1-6,00:00-23:00", false)]
    [InlineData("1;wd1-5h9-18", true)]
    [InlineData("0;h9m/30;h10", true)]
    [InlineData("0;h9m/30;h10;1m/1-6,00:00-23:00;h9m10-40/1;1/1-7,12:00-12:01", true)]
    [InlineData("a;wd1-5h9-18", false)]
    [InlineData("1;x", false)]
    [InlineData("1;;0/wd1-5h9-18", false)]
    [InlineData("1;wd1-5h9-18;", false)]
    [InlineData("wd1-5h9-18", false)]
    [InlineData("1;wd1-5h9-18/wd1-5h9-18", false)]
    [InlineData("86401;h9m/30", false)]
    [InlineData("86400;h9m/30", true)]
    [InlineData("", false)]
    // zbx_custom_interval_is_scheduling.yaml
    [InlineData("1m;0/1-7,00:00-13:00;0/1-7,13:00-24:00;wd1h15-18", true)]
    [InlineData("1;m0-59;h9-17/2;m0,30;m0,5,10,15,20,25,30,35,40,45,50,55;wd1-5h9-18;h9,10,11;md1h9m30;md1wd1h9m30;h9m0-59/30;h9m10-40/30;h9-10m10-40/30;h9m10-40/1;h9-12,15m0;h9-12,15m0s30;h9-12s30", true)]
    public void A_delay_parses_exactly_when_upstream_s_preprocessor_accepts_it(string text, bool valid)
        => Assert.Equal(valid, ZabbixItemDelay.TryParse(text, CultureInfo.InvariantCulture, out _));

    [Theory]
    [InlineData("1m;0/1-7,00:00-13:00;0/1-7,13:00-24:00;wd1h15-18", true)]
    [InlineData("1m", false)]
    [InlineData("50s;10s/1-5,09:00-18:00", false)]
    public void A_scheduling_interval_is_recognised(string text, bool scheduling)
        => Assert.Equal(scheduling, ZabbixItemDelay.Parse(text, CultureInfo.InvariantCulture).IsScheduling);

    [Theory]
    [InlineData("0", "Invalid update interval \"0\".")]
    [InlineData("1w", "Invalid update interval \"1w\".")]
    [InlineData("1;0x1-6,00:00-23:00", "Invalid flexible interval \"0x1-6,00:00-23:00\".")]
    [InlineData("1;x", "Invalid scheduling interval \"x\".")]
    [InlineData("0;0/1-6,00:00-23:00", "Invalid flexible interval \"0/1-6,00:00-23:00\".")]
    public void A_bad_delay_fails_in_upstream_s_words(string text, string message)
        => Assert.Equal(message, Assert.Throws<FormatException>(() => ZabbixItemDelay.Parse(text, CultureInfo.InvariantCulture)).Message);

    // zbx_get_agent_item_nextcheck.yaml: what an agent schedules; the phase is the item id, the clock UTC. The
    // "over Jan 2038" cases are compiled out upstream (TIME_IS_32INT is always defined in zbxmocktest.h), and one of
    // them ("Delay 1s") contradicts its siblings, so only the two consistent ones are kept.
    [Theory]
    [InlineData(1000L, "1s", "2023-12-18T14:30:00Z", 1702909801L)]
    [InlineData(1L, "10s", "2023-12-18T14:30:00Z", 1702909801L)]
    [InlineData(123L, "1d", "2023-12-18T14:30:00Z", 1702944123L)]
    [InlineData(1000L, "10s", "2040-12-18T14:30:00Z", 2239453810L)]
    [InlineData(1L, "1d", "2040-12-18T14:30:00Z", 2239488001L)]
    public void The_agent_s_next_check_is_phased_by_the_item_id(long itemId, string delay, string now, long expected)
    {
        DateTimeOffset? next = ZabbixItemDelay.Parse(delay, CultureInfo.InvariantCulture)
            .GetNextCheck(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture), TimeZoneInfo.Utc, itemId);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expected), next);
    }

    // calculate_item_nextcheck.yaml, item type ZABBIX (the polled type an agent's schedule uses; the ZABBIX_ACTIVE
    // cases are the server's queue view of an active item, which no agent computes), seed 0. Each expected instant
    // is fed back in as the next "now", as the upstream harness does.
    public static TheoryData<string, string, string, string> Schedules => new()
    {
        { "0;m/15", "Europe/Riga", "2017-10-29 03:00:00 +03:00", "2017-10-29 03:15:00 +03:00|2017-10-29 03:30:00 +03:00|2017-10-29 03:45:00 +03:00|2017-10-29 03:00:00 +02:00|2017-10-29 03:15:00 +02:00|2017-10-29 03:30:00 +02:00" },
        { "0;h/1", "Europe/Riga", "2017-10-29 03:00:00 +03:00", "2017-10-29 03:00:00 +02:00|2017-10-29 04:00:00 +02:00|2017-10-29 05:00:00 +02:00" },
        { "0;h9", "Europe/Riga", "2017-10-29 03:00:00 +03:00", "2017-10-29 09:00:00 +02:00|2017-10-30 09:00:00 +02:00" },
        { "0;m15", "Europe/Riga", "2017-10-29 03:00:00 +03:00", "2017-10-29 03:15:00 +03:00|2017-10-29 03:15:00 +02:00|2017-10-29 04:15:00 +02:00|2017-10-29 05:15:00 +02:00" },
        { "0;h3m30", "Europe/Riga", "2017-10-29 03:00:00 +03:00", "2017-10-29 03:30:00 +03:00|2017-10-29 03:30:00 +02:00|2017-10-30 03:30:00 +02:00" },
        { "0;h/2", "Europe/Riga", "2017-10-29 01:00:00 +03:00", "2017-10-29 02:00:00 +03:00|2017-10-29 04:00:00 +02:00|2017-10-29 06:00:00 +02:00" },
        { "0;m/15", "Europe/Riga", "2017-03-26 02:00:00 +02:00", "2017-03-26 02:15:00 +02:00|2017-03-26 02:30:00 +02:00|2017-03-26 02:45:00 +02:00|2017-03-26 04:00:00 +03:00|2017-03-26 04:15:00 +03:00|2017-03-26 04:30:00 +03:00" },
        { "0;h/1", "Europe/Riga", "2017-03-26 01:00:00 +02:00", "2017-03-26 02:00:00 +02:00|2017-03-26 04:00:00 +03:00|2017-03-26 05:00:00 +03:00|2017-03-26 06:00:00 +03:00" },
        { "0;h9", "Europe/Riga", "2017-03-26 02:00:00 +02:00", "2017-03-26 09:00:00 +03:00|2017-03-27 09:00:00 +03:00" },
        { "0;m15", "Europe/Riga", "2017-03-26 02:00:00 +02:00", "2017-03-26 02:15:00 +02:00|2017-03-26 04:15:00 +03:00|2017-03-26 05:15:00 +03:00" },
        { "0;h3m30", "Europe/Riga", "2017-03-25 02:00:00 +02:00", "2017-03-25 03:30:00 +02:00|2017-03-27 03:30:00 +03:00|2017-03-28 03:30:00 +03:00" },
        { "0;h/2", "Europe/Riga", "2017-03-26 01:00:00 +03:00", "2017-03-26 02:00:00 +02:00|2017-03-26 04:00:00 +03:00|2017-03-26 06:00:00 +03:00" },
        { "0;h3", "Europe/Riga", "2017-03-25 02:00:00 +02:00", "2017-03-25 03:00:00 +02:00|2017-03-27 03:00:00 +03:00|2017-03-28 03:00:00 +03:00" },
        { "1m", "ZBX1:30", "2018-01-01 00:00:00 -01:30", "2018-01-01 00:01:00 -01:30" },
        { "1m", "ZBX0:45", "2018-01-01 00:00:00 -00:45", "2018-01-01 00:01:00 -00:45" },
        { "0;md1wd6", "UTC", "1970-01-01 00:00:00 +00:00", "1970-08-01 00:00:00 +00:00" },
        { "0;md1,31", "UTC", "1970-02-01 00:00:00 +00:00", "1970-03-01 00:00:00 +00:00|1970-03-31 00:00:00 +00:00" },
        { "0;h2", "Pacific/Apia", "2011-12-29 00:00:00 -10:00", "2011-12-29 02:00:00 -10:00|2011-12-31 02:00:00 +14:00|2012-01-01 02:00:00 +14:00" },
        { "0;h0,10,14,17,20m30", "America/Sao_Paulo", "2018-02-17 20:00:00 -02:00", "2018-02-17 20:30:00 -02:00|2018-02-18 00:30:00 -03:00|2018-02-18 10:30:00 -03:00" },
        { "0;h0,10,11,20m30", "Europe/Moscow", "1981-09-30 20:00:00 +04:00", "1981-09-30 20:30:00 +04:00|1981-10-01 00:30:00 +03:00|1981-10-01 10:30:00 +03:00" },
        { "0;h0,0m59;h10,20m30", "Europe/Moscow", "1981-03-31 20:00:00 +03:00", "1981-03-31 20:30:00 +03:00|1981-04-01 10:30:00 +04:00" },
    };

    [Theory]
    [MemberData(nameof(Schedules))]
    public void The_schedule_matches_upstream_across_time_zones_and_daylight_saving_changes(string delay, string zone, string at, string checks)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() && zone == "Europe/Moscow", "Windows' own zone data does not have IANA's Moscow offsets for 1981.");
        ZabbixItemDelay parsed = ZabbixItemDelay.Parse(delay, CultureInfo.InvariantCulture);
        TimeZoneInfo timeZone = Zone(zone);
        DateTimeOffset now = Instant(at);
        var actual = new List<DateTimeOffset>();
        string[] expected = checks.Split('|');

        foreach (string _ in expected)
        {
            now = parsed.GetNextCheck(now, timeZone) ?? throw new InvalidOperationException("never due");
            actual.Add(now);
        }

        Assert.Equal(expected.Select(Instant), actual);
    }

    [Fact]
    public void Without_a_seed_the_schedule_is_the_unphased_one_and_takes_the_seed_by_overload_not_by_default()
    {
        ZabbixItemDelay delay = ZabbixItemDelay.Parse("50s", CultureInfo.InvariantCulture);
        DateTimeOffset now = Instant("2026-09-23 10:00:07 +00:00");

        Assert.Equal(delay.GetNextCheck(now, TimeZoneInfo.Utc, 0), delay.GetNextCheck(now, TimeZoneInfo.Utc));
        Assert.All(
            typeof(ZabbixItemDelay).GetMethods().Where(m => m.Name == nameof(ZabbixItemDelay.GetNextCheck)).SelectMany(m => m.GetParameters()),
            parameter => Assert.False(parameter.HasDefaultValue));
    }

    // get_current_delay / get_next_delay_interval, by hand from interval.c: upstream collects "1h;3/1-7,00:00-24:00"
    // every 3 s (measured live), not once an hour.
    [Fact]
    public void A_flexible_interval_replaces_the_update_interval_while_its_period_lasts()
    {
        ZabbixItemDelay delay = ZabbixItemDelay.Parse("1h;3/1-7,00:00-24:00", CultureInfo.InvariantCulture);
        DateTimeOffset now = Instant("2026-09-23 10:00:00 +00:00");

        Assert.Equal(now.AddSeconds(3), delay.GetNextCheck(now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Outside_its_period_the_update_interval_applies_and_a_period_starting_sooner_wins()
    {
        // Weekdays 09:00-18:00 every 10 s, otherwise hourly. Wednesday 08:30: the hour would end at 09:00 exactly,
        // which is when the flexible period starts, so the check moves to the period's first 10 s slot.
        ZabbixItemDelay delay = ZabbixItemDelay.Parse("1h;10s/1-5,09:00-18:00", CultureInfo.InvariantCulture);

        Assert.Equal(Instant("2026-09-23 09:00:00 +00:00"), delay.GetNextCheck(Instant("2026-09-23 08:30:00 +00:00"), TimeZoneInfo.Utc));
        Assert.Equal(Instant("2026-09-23 12:00:10 +00:00"), delay.GetNextCheck(Instant("2026-09-23 12:00:00 +00:00"), TimeZoneInfo.Utc));
        Assert.Equal(Instant("2026-09-26 13:00:00 +00:00"), delay.GetNextCheck(Instant("2026-09-26 12:00:00 +00:00"), TimeZoneInfo.Utc));   // Saturday
    }

    [Fact]
    public void A_delay_that_is_zero_everywhere_is_never_due()
    {
        // Hourly, except every day all day, where the delay is 0: nothing within a year (upstream's ZBX_JAN_2038).
        ZabbixItemDelay delay = ZabbixItemDelay.Parse("1h;0/1-7,00:00-24:00", CultureInfo.InvariantCulture);

        Assert.Null(delay.GetNextCheck(Instant("2026-09-23 12:00:00 +00:00"), TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_delay_is_the_text_the_server_holds()
    {
        ZabbixItemDelay delay = ZabbixItemDelay.Parse("50s;10s/1-5,09:00-18:00", CultureInfo.InvariantCulture);

        Assert.Equal("50s;10s/1-5,09:00-18:00", delay.ToString());
        Assert.Equal(TimeSpan.FromSeconds(50), delay.UpdateInterval);
        Assert.Equal(delay, ZabbixItemDelay.Parse("50s;10s/1-5,09:00-18:00", CultureInfo.InvariantCulture));
    }

    private static DateTimeOffset Instant(string text) => DateTimeOffset.ParseExact(text, "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    /// <summary>An IANA zone, or a POSIX <c>NAMEh:mm</c> fixed zone (hours west of UTC, as <c>TZ=ZBX1:30</c> means).</summary>
    private static TimeZoneInfo Zone(string name)
    {
        if (!name.StartsWith("ZBX", StringComparison.Ordinal)) return TimeZoneInfo.FindSystemTimeZoneById(name);
        TimeSpan west = TimeSpan.ParseExact(name[3..], @"h\:mm", CultureInfo.InvariantCulture);
        return TimeZoneInfo.CreateCustomTimeZone(name, -west, name, name);
    }
}
