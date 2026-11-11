// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Protocol;

namespace Community.Zabbix.Tests;

/// <summary>
/// The wire's spans and durations say which request they measured (<c>rpc.method</c>), so a proxy's configuration pull, a
/// sender frame and an active-checks refresh are separate series; a sampler sees the span's tags when it decides; and the
/// activity source and the meter carry the library's version as their instrumentation scope's.
/// </summary>
public sealed class WireTelemetryScopeTests
{
    [Theory]
    [InlineData("""{"request":"sender data","data":[]}""", "sender data")]
    [InlineData("""{"data":{"request":"history.push"},"request":"proxy config","host":"p"}""", "proxy config")]
    [InlineData("""{"request":"active checks","host":"h"}""", "active checks")]
    [InlineData("""{"request":"a caller's own words"}""", "_OTHER")]
    [InlineData("""{"request":7}""", "_OTHER")]
    [InlineData("""{"host":"h"}""", "_OTHER")]
    [InlineData("""{"request":"sender da""", "_OTHER")]
    [InlineData("system.cpu.load[all,avg1]", "_OTHER")]
    [InlineData("", "_OTHER")]
    public void An_exchange_is_named_for_the_protocol_request_it_carries(string payload, string expected)
        => Assert.Equal(expected, ZabbixRequestName.Of(System.Text.Encoding.UTF8.GetBytes(payload)));

    [Fact]
    public void A_sampler_sees_the_exchange_s_tags_and_the_source_s_version()
    {
        var sampled = new ConcurrentBag<Dictionary<string, object?>>();
        var versions = new ConcurrentBag<string?>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ZabbixTelemetryNames.Wire,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                sampled.Add(options.Tags?.ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal) ?? []);
                versions.Add(options.Source.Version);
                return ActivitySamplingResult.AllDataAndRecorded;
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var telemetry = new ZabbixWireTelemetry(null);

        ZabbixOperation operation = telemetry.StartExchange(notify: false, ZabbixTlsMode.PreSharedKey, """{"request":"proxy config"}"""u8);
        telemetry.Finish(operation);

        Assert.Contains(sampled, tags => tags.GetValueOrDefault("rpc.method") is "proxy config" && tags.GetValueOrDefault("zabbix.tls.mode") is "psk"
            && tags.GetValueOrDefault("network.transport") is "tcp");
        Assert.All(versions, version => Assert.False(string.IsNullOrEmpty(version)));
        Assert.Equal(ZabbixLayerTelemetry.ScopeVersion(typeof(ZabbixWireTelemetry).Assembly), versions.First());
    }

    [Fact]
    public void The_meter_carries_the_library_s_version_without_build_metadata()
    {
        var versions = new ConcurrentBag<string?>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == ZabbixTelemetryNames.Wire) versions.Add(instrument.Meter.Version);
            },
        };
        listener.Start();

        using var telemetry = new ZabbixWireTelemetry(null);

        string? expected = ZabbixLayerTelemetry.ScopeVersion(typeof(ZabbixWireTelemetry).Assembly);
        Assert.False(string.IsNullOrEmpty(expected));
        Assert.DoesNotContain('+', expected);
        Assert.Contains(expected, versions);
    }
}
