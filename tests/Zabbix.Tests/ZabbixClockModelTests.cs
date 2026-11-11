// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Community.Zabbix.Connector;
using Community.Zabbix.Protocol;
using Community.Zabbix.Proxy;

namespace Community.Zabbix.Tests;

/// <summary>
/// Every instant is a <see cref="DateTimeOffset"/> over an exact <see cref="ZabbixClock"/>: a clock and its nanoseconds (<c>clock</c> and <c>ns</c>) are one member
/// whose wire keeps the two fields, a clock alone is the second it names, and a date is a <see cref="DateOnly"/>.
/// </summary>
public sealed class ZabbixClockModelTests
{
    [Fact]
    public void An_events_clock_and_ns_read_into_one_instant_and_write_back_as_the_two_fields()
    {
        ZabbixEvent read = JsonSerializer.Deserialize<ZabbixEvent>("""{"eventid":"7","clock":"1700000000","ns":"501"}""", ZabbixJson.Options)!;

        Assert.Equal(new ZabbixClock(1_700_000_000, 501), read.ClockExact);
        string written = JsonSerializer.Serialize(read with { Clock = DateTimeOffset.FromUnixTimeSeconds(1_700_000_001).AddTicks(2) }, ZabbixJson.Options);
        Assert.Contains("\"clock\":1700000001", written, StringComparison.Ordinal);
        Assert.Contains("\"ns\":200", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_problem_has_its_clock_and_its_recovery_clock_each_to_the_nanosecond()
    {
        ZabbixProblem read = JsonSerializer.Deserialize<ZabbixProblem>("""{"eventid":"7","clock":"10","ns":"1","r_clock":"20","r_ns":"2"}""", ZabbixJson.Options)!;

        Assert.Equal((new ZabbixClock(10, 1), new ZabbixClock(20, 2)), (read.ClockExact, read.RecoveryClockExact));
    }

    [Fact]
    public void A_clock_without_its_ns_is_the_whole_second_and_absent_is_null()
    {
        Assert.Equal(new ZabbixClock(1_700_000_000, 0), JsonSerializer.Deserialize<ZabbixEvent>("""{"clock":1700000000}""", ZabbixJson.Options)!.ClockExact);
        Assert.Null(JsonSerializer.Deserialize<ZabbixEvent>("{}", ZabbixJson.Options)!.Clock);
    }

    [Theory]
    [InlineData("""{"clock":"1","ns":"1000000000"}""")]
    [InlineData("""{"clock":"1","ns":"-1"}""")]
    public void Nanoseconds_outside_the_second_are_not_the_protocol(string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ZabbixEvent>(json, ZabbixJson.Options));

    [Fact]
    public void Two_events_of_one_instant_are_equal()
    {
        var instant = new ZabbixClock(1_700_000_000, 500);

        Assert.Equal(new ZabbixEvent { EventId = 1, Clock = instant.ToDateTimeOffset() }, new ZabbixEvent { EventId = 1, Clock = instant.ToDateTimeOffset() });
        Assert.NotEqual(new ZabbixEvent { EventId = 1, Clock = instant.ToDateTimeOffset() }, new ZabbixEvent { EventId = 1, Clock = new ZabbixClock(1_700_000_000, 600).ToDateTimeOffset() });
    }

    [Theory]
    [InlineData("1700000000")]
    [InlineData("\"1700000000\"")]
    public void A_seconds_only_time_reads_from_a_number_or_its_text_and_writes_the_number(string form)
    {
        ZabbixHost host = JsonSerializer.Deserialize<ZabbixHost>("{\"maintenance_from\":" + form + "}", ZabbixJson.Options)!;

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), host.MaintenanceFrom);
        Assert.Contains("\"maintenance_from\":1700000000", JsonSerializer.Serialize(host, ZabbixJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public void A_get_takes_its_time_bounds_as_clocks()
    {
        string sent = JsonSerializer.Serialize(new ZabbixEventGetParameters { TimeFrom = new ZabbixClock(10, 999).ToDateTimeOffset(), TimeTo = DateTimeOffset.FromUnixTimeSeconds(20) }, ZabbixJson.Options);

        Assert.Contains("\"time_from\":10", sent, StringComparison.Ordinal);
        Assert.Contains("\"time_till\":20", sent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"2026-10-09\"", "2026-10-09")]
    [InlineData("\"\"", null)]
    [InlineData("null", null)]
    public void A_reports_active_since_is_a_date(string form, string? expected)
    {
        ZabbixReport report = JsonSerializer.Deserialize<ZabbixReport>("{\"active_since\":" + form + "}", ZabbixJson.Options)!;

        Assert.Equal(expected is null ? null : DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), report.ActiveSince);
    }

    [Fact]
    public void A_date_is_written_as_the_API_takes_it()
        => Assert.Contains("\"active_since\":\"2026-10-09\"", JsonSerializer.Serialize(new ZabbixReport { ActiveSince = new DateOnly(2026, 10, 9) }, ZabbixJson.Options), StringComparison.Ordinal);

    [Theory]
    [InlineData("\"7200\"", 2, 0)]
    [InlineData("7200", 2, 0)]
    [InlineData("0", 0, 0)]
    [InlineData("86340", 23, 59)]
    public void A_time_of_day_reads_as_seconds_after_midnight(string form, int hour, int minute)
    {
        ZabbixReport report = JsonSerializer.Deserialize<ZabbixReport>("{\"start_time\":" + form + "}", ZabbixJson.Options)!;

        Assert.Equal(new TimeOnly(hour, minute), report.StartTime);
    }

    [Theory]
    [InlineData("86400")]
    [InlineData("-1")]
    [InlineData("\"02:00\"")]
    [InlineData("1.5")]
    public void A_time_of_day_outside_one_day_is_refused(string form)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ZabbixMaintenanceRecordTimePeriod>("{\"start_time\":" + form + "}", ZabbixJson.Options));

    [Fact]
    public void A_time_of_day_is_written_as_whole_seconds_after_midnight()
        => Assert.Contains("\"start_time\":7260", JsonSerializer.Serialize(new ZabbixMaintenanceTimePeriodRequest { StartTime = new TimeOnly(2, 1, 0, 500) }, ZabbixJson.Options), StringComparison.Ordinal);

    [Fact]
    public void The_framed_protocol_keeps_clock_and_ns_on_the_wire()
    {
        var row = new ZabbixProxyHistoryItem(1, 7UL, new ZabbixClock(1_700_000_000, 123), "1");
        var sent = new ZabbixSenderItem("h", "k", "v", new ZabbixClock(1_700_000_000, 456));

        Assert.Contains("\"clock\":1700000000,\"ns\":123", JsonSerializer.Serialize(row, ZabbixProxyJson.Default.ZabbixProxyHistoryItem), StringComparison.Ordinal);
        Assert.Contains("\"clock\":1700000000,\"ns\":456", JsonSerializer.Serialize(sent, ZabbixSenderJson.Default.ZabbixSenderItem), StringComparison.Ordinal);
    }

    [Fact]
    public void A_queue_row_s_next_check_is_the_second_the_server_names()
    {
        ZabbixQueuedItem row = JsonSerializer.Deserialize("""{"itemid":42,"nextcheck":1700000000}""", ZabbixServerWireJson.Default.ZabbixQueuedItem)!;

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), row.NextCheck);
    }

    [Fact]
    public void A_connector_line_is_one_instant()
    {
        ZabbixConnectorRecord line = JsonSerializer.Deserialize<ZabbixConnectorRecord>("""{"itemid":1,"clock":1700000000,"ns":501,"value":"1"}""", ZabbixConnectorJson.Default.ZabbixConnectorRecord)!;

        Assert.Equal(new ZabbixClock(1_700_000_000, 501), line.Exact);
    }
}
