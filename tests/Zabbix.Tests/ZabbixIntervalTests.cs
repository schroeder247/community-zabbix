// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;

namespace Community.Zabbix.Tests;

/// <summary>
/// <see cref="ZabbixInterval"/> and <see cref="ZabbixDuration"/>, Zabbix's grammar of time as values: a
/// <see cref="TimeSpan"/> converts to the spelling with the largest suffix that divides it, <c>Parse</c> takes the rest of
/// the grammar and keeps a macro as it's written, and the wire carries the text both ways, a reply's text kept even where
/// it doesn't fit the grammar.
/// </summary>
public sealed class ZabbixIntervalTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(30, "30s")]
    [InlineData(90, "90s")]
    [InlineData(300, "5m")]
    [InlineData(5400, "90m")]
    [InlineData(3600, "1h")]
    [InlineData(86400, "1d")]
    [InlineData(604800, "1w")]
    [InlineData(1209600, "2w")]
    [InlineData(int.MaxValue, "2147483647s")]
    public void A_TimeSpan_is_written_with_the_largest_suffix_that_divides_it(int seconds, string text)
    {
        ZabbixInterval interval = TimeSpan.FromSeconds(seconds);
        ZabbixDuration duration = TimeSpan.FromSeconds(seconds);

        Assert.Equal(text, interval.ToString());
        Assert.Equal(text, duration.ToString());
        Assert.Equal(TimeSpan.FromSeconds(seconds), interval.UpdateInterval);
        Assert.Equal(TimeSpan.FromSeconds(seconds), duration.Length);
        Assert.Equal(interval, ZabbixInterval.FromTimeSpan(TimeSpan.FromSeconds(seconds)));
        Assert.Equal(duration, ZabbixDuration.FromTimeSpan(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(-10_000_000L)]              // minus one second
    [InlineData(15_000_000L)]               // a second and a half
    [InlineData(1L)]                        // one tick
    [InlineData(21_474_836_480_000_000L)]   // 2^31 seconds, one past what Zabbix reads
    public void A_TimeSpan_Zabbix_cannot_write_is_refused_naming_the_parameter(long ticks)
    {
        TimeSpan span = TimeSpan.FromTicks(ticks);

        Assert.Equal("interval", Assert.Throws<ArgumentOutOfRangeException>(() => ZabbixInterval.FromTimeSpan(span)).ParamName);
        Assert.Equal("length", Assert.Throws<ArgumentOutOfRangeException>(() => ZabbixDuration.FromTimeSpan(span)).ParamName);
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("60", 60)]
    [InlineData("0", 0)]
    [InlineData("1m;10s/1-5,09:00-18:00", 60)]
    [InlineData("0;wd1-5h9-18", 0)]
    [InlineData("1h;md1,15h0m30;10s/6-7,00:00-24:00", 3600)]
    [InlineData("10m;{$FLEX}", 600)]
    [InlineData("5m;30s/{$OFFICE_HOURS}", 300)]
    [InlineData("{$DELAY}", null)]
    [InlineData("{$DELAY:\"web, db\"}", null)]
    [InlineData("{#DELAY}", null)]
    [InlineData("{$DELAY};{$FLEX_DELAY}/{$FLEX_PERIOD}", null)]
    public void Parse_reads_Zabbix_s_grammar_and_keeps_the_text_as_written(string text, int? seconds)
    {
        ZabbixInterval interval = ZabbixInterval.Parse(text);

        Assert.Equal(text, interval.ToString());
        Assert.Equal(seconds is { } whole ? TimeSpan.FromSeconds(whole) : null, interval.UpdateInterval);
        Assert.True(ZabbixInterval.TryParse(text, out ZabbixInterval tried));
        Assert.Equal(interval, tried);
        Assert.Equal(interval, ParseAsParsable<ZabbixInterval>(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 30s")]
    [InlineData("30S")]
    [InlineData("1y")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("30s;")]
    [InlineData("1m;10s")]
    [InlineData("1m;10s/8,09:00-18:00")]
    [InlineData("1m;10s/5-1,09:00-18:00")]
    [InlineData("1m;10s/1-5,18:00-09:00")]
    [InlineData("1m;10s/1-5,09:00-24:01")]
    [InlineData("1m;10s/1-5,09:60-18:00")]
    [InlineData("1m;wd8")]
    [InlineData("1m;h9wd1")]
    [InlineData("{$DELAY")]
    [InlineData("{$delay}")]
    [InlineData("{HOST.HOST}")]
    public void Parse_refuses_text_outside_Zabbix_s_grammar(string text)
    {
        Assert.False(ZabbixInterval.TryParse(text, out ZabbixInterval result));
        Assert.Equal(default, result);
        FormatException refused = Assert.Throws<FormatException>(() => ZabbixInterval.Parse(text));
        Assert.StartsWith($"'{text}' isn't an interval Zabbix takes", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("90d", 90 * 86400)]
    [InlineData("3s", 3)]
    [InlineData("0", 0)]
    [InlineData("25w", 25 * 604800)]
    [InlineData("{$HISTORY}", null)]
    [InlineData("{#TRENDS}", null)]
    [InlineData("", null)]
    public void A_length_parses_a_number_a_suffix_a_macro_or_Zabbix_s_empty_value(string text, int? seconds)
    {
        ZabbixDuration duration = ZabbixDuration.Parse(text);

        Assert.Equal(text, duration.ToString());
        Assert.Equal(seconds is { } whole ? TimeSpan.FromSeconds(whole) : null, duration.Length);
        Assert.Equal(duration, ParseAsParsable<ZabbixDuration>(text));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("90 d")]
    [InlineData("1y")]
    [InlineData("-1")]
    [InlineData("1m;wd1")]
    [InlineData("1m;10s/1-5,09:00-18:00")]
    [InlineData("{HOST.HOST}")]
    public void A_length_refuses_an_update_interval_s_extras_and_text_outside_the_grammar(string text)
    {
        Assert.False(ZabbixDuration.TryParse(text, out _));
        Assert.StartsWith($"'{text}' isn't a length of time Zabbix takes", Assert.Throws<FormatException>(() => ZabbixDuration.Parse(text)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_is_refused_and_TryParse_says_no()
    {
        Assert.Throws<ArgumentNullException>(() => ZabbixInterval.Parse((string)null!));
        Assert.False(ZabbixInterval.TryParse((string?)null, out _));
        Assert.Throws<ArgumentNullException>(() => ZabbixDuration.Parse((string)null!));
        Assert.False(ZabbixDuration.TryParse((string?)null, out _));
    }

    [Fact]
    public void Values_are_equal_when_written_the_same_and_the_default_is_zero()
    {
        ZabbixInterval minute = TimeSpan.FromMinutes(1);

        Assert.Equal(ZabbixInterval.Parse("1m"), minute);
        Assert.NotEqual(ZabbixInterval.Parse("60"), minute);
        Assert.Equal(minute.UpdateInterval, ZabbixInterval.Parse("60").UpdateInterval);
        Assert.Equal("0", default(ZabbixInterval).ToString());
        Assert.Equal(default, ZabbixInterval.Parse("0"));
        Assert.Equal(TimeSpan.Zero, default(ZabbixInterval).UpdateInterval);
        Assert.Equal("0", default(ZabbixDuration).ToString());
        Assert.Equal(default, (ZabbixDuration)TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, default(ZabbixDuration).Length);
    }

    [Fact]
    public void A_request_sends_the_text()
    {
        var update = new ZabbixItemUpdateRequest(7)
        {
            Delay = ZabbixInterval.Parse("1m;10s/1-5,09:00-18:00"),
            History = TimeSpan.FromDays(90),
            Trends = TimeSpan.Zero,
            Timeout = ZabbixDuration.Parse("{$AGENT.TIMEOUT}"),
        };

        using JsonDocument sent = JsonDocument.Parse(JsonSerializer.Serialize(update, ZabbixApiJson.Default.ZabbixItemUpdateRequest));

        Assert.Equal("1m;10s/1-5,09:00-18:00", sent.RootElement.GetProperty("delay").GetString());
        Assert.Equal("90d", sent.RootElement.GetProperty("history").GetString());
        Assert.Equal("0", sent.RootElement.GetProperty("trends").GetString());
        Assert.Equal("{$AGENT.TIMEOUT}", sent.RootElement.GetProperty("timeout").GetString());
    }

    [Fact]
    public void A_reply_s_text_or_number_is_read_and_text_outside_the_grammar_is_kept_as_written()
    {
        ZabbixItem item = JsonSerializer.Deserialize(
            """{"itemid":"7","delay":"{$DELAY}","history":"90d","trends":0,"timeout":""}""", ZabbixApiJson.Default.ZabbixItem)!;
        ZabbixItem newer = JsonSerializer.Deserialize(
            """{"itemid":"8","delay":"1m;some newer grammar","history":"forever"}""", ZabbixApiJson.Default.ZabbixItem)!;

        Assert.Equal(ZabbixInterval.Parse("{$DELAY}"), item.Delay);
        Assert.Null(item.Delay!.Value.UpdateInterval);
        Assert.Equal(TimeSpan.FromDays(90), item.History!.Value.Length);
        Assert.Equal(default(ZabbixDuration), item.Trends);
        Assert.Equal(string.Empty, item.Timeout!.Value.ToString());
        Assert.Null(item.Timeout!.Value.Length);
        Assert.Equal("1m;some newer grammar", newer.Delay!.Value.ToString());
        Assert.Equal(TimeSpan.FromMinutes(1), newer.Delay!.Value.UpdateInterval);
        Assert.Equal("forever", newer.History!.Value.ToString());
        Assert.Null(newer.History!.Value.Length);
        Assert.Equal(
            """{"itemid":"8","delay":"1m;some newer grammar","history":"forever"}""",
            JsonSerializer.Serialize(newer, ZabbixApiJson.Default.ZabbixItem));
    }

    private static T ParseAsParsable<T>(string text) where T : ISpanParsable<T>
    {
        T parsed = T.Parse(text, CultureInfo.InvariantCulture);
        Assert.True(T.TryParse(text.AsSpan(), CultureInfo.InvariantCulture, out T? spanned));
        Assert.Equal(parsed, spanned);
        return parsed;
    }
}
