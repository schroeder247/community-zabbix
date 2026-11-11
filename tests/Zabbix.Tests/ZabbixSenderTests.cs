// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Community.Zabbix.Hosting;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>The sender protocol: the frame, the request, the parsed reply, the log events, and interop with the live lab.</summary>
public sealed class ZabbixSenderTests
{
    [Fact]
    public async Task A_frame_round_trips_a_payload()
    {
        using var stream = new MemoryStream();
        byte[] payload = Encoding.UTF8.GetBytes("""{"request":"sender data","data":[]}""");

        await ZabbixHeader.WriteAsync(stream, payload, TestContext.Current.CancellationToken);
        stream.Position = 0;
        ReadOnlyMemory<byte> read = await ZabbixHeader.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(payload, read.ToArray());
        Assert.Equal(13 + payload.Length, stream.Length);
    }

    [Fact]
    public async Task A_frame_that_is_not_zabbix_is_refused()
    {
        using var stream = new MemoryStream("HTTP/1.1 200 OK\r\n\r\n"u8.ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(async () => await ZabbixHeader.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Values_are_sent_as_the_wire_schema_and_the_reply_is_parsed()
    {
        var (port, received) = MockTrapper.Collect(2);
        using var sender = new ZabbixSender("127.0.0.1", port);
        ZabbixValue[] values =
        [
            new("web01", "system.cpu.load[all,avg1]", "0.42", DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000).AddTicks(1_234_567)),
            new("web01", "vfs.fs.size[/,used]", "1024", DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_001)),
        ];

        ZabbixSenderResponse response = await sender.SendAsync(values, TestContext.Current.CancellationToken);

        Assert.False(response.HasFailures);
        Assert.Equal(2, response.Processed);
        Assert.Equal(2, response.Total);
        Assert.Equal(TimeSpan.FromSeconds(double.Parse(response.Info[(response.Info.LastIndexOf(' ') + 1)..], System.Globalization.CultureInfo.InvariantCulture)), response.TimeSpent);
        Assert.Equal(2, await received);
    }

    [Fact]
    public void A_sample_projects_to_the_wire_with_clock_and_nanoseconds_split()
    {
        var clock = new ZabbixClock(1_787_054_400, 123_456_789);

        ZabbixSenderItem wire = ZabbixSenderItem.From(new ZabbixValue("web01", "system.cpu.load[all,avg1]", "0.42", clock));

        Assert.Equal("web01", wire.Host);
        Assert.Equal("system.cpu.load[all,avg1]", wire.Key);
        Assert.Equal(clock, wire.Exact);                                                     // every nanosecond reaches the wire
        Assert.Contains("\"clock\":1787054400,\"ns\":123456789", System.Text.Json.JsonSerializer.Serialize(wire, ZabbixWireJson.Default.ZabbixSenderItem), StringComparison.Ordinal);
    }

    [Fact]
    public void The_wire_schema_is_derived_from_the_records_json_contract()
    {
        JsonNode schema = JsonSchemaExporter.GetJsonSchemaAsNode(ZabbixWireJson.Default.ZabbixSenderItem);

        JsonObject properties = schema["properties"]!.AsObject();
        Assert.Equal(["host", "key", "value", "clock", "ns", "state"], properties.Select(property => property.Key));
        Assert.Equal("integer", schema["properties"]!["clock"]!["type"]!.ToString());
    }

    [Fact]
    public async Task A_sent_frame_is_logged_with_the_servers_counters()
    {
        var (port, _) = MockTrapper.Collect(1, processed: 0);
        var logs = new FakeLogCollector();
        using var sender = new ZabbixSender([new ZabbixServerNode("127.0.0.1", port)], null, null, new FakeLogger<ZabbixSender>(logs));

        await sender.SendAsync([new ZabbixValue("unknown", "k", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);

        FakeLogRecord sent = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 1001);
        Assert.Equal(LogLevel.Debug, sent.Level);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1002 && record.Level == LogLevel.Warning);
    }

    [Fact]
    public void An_agent_with_no_server_fails_at_start()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Community.Zabbix.ZabbixPushAgentServiceCollectionExtensions.AddZabbixPushAgent(services, "lonely");
        using Microsoft.Extensions.DependencyInjection.ServiceProvider provider = services.BuildValidated();

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.Options.IStartupValidator>(provider).Validate());
    }

    // Longer than the two seconds Windows retries a refused connection for, so a refusal is not mistaken for a timeout.
    private static readonly TimeSpan RefusedWithin = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_refused_connection_is_logged_and_thrown()
    {
        var logs = new FakeLogCollector();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var sender = new ZabbixSender([new ZabbixServerNode("127.0.0.1", port)], new ZabbixConnectionOptions { Timeout = RefusedWithin }, null, new FakeLogger<ZabbixSender>(logs));

        await Assert.ThrowsAsync<SocketException>(async () => await sender.SendAsync([new ZabbixValue("h", "k", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken));

        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1003 && record.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task A_refused_connection_is_a_retry_verdict_not_a_dropped_exception()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var sender = new ZabbixSender([new ZabbixServerNode("127.0.0.1", port)], new ZabbixConnectionOptions { Timeout = RefusedWithin });

        ZabbixDelivery delivery = await sender.DeliverAsync([new ZabbixValue("h", "k", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);

        Assert.Equal(ZabbixDelivery.Retried, delivery);
    }

    [Fact]
    public void The_deliver_retry_filter_matches_every_exchange_failure_type()
    {
        Type[] types = [typeof(SocketException), typeof(IOException), typeof(AuthenticationException), typeof(InvalidDataException), typeof(TimeoutException)];
        foreach (Type type in types)
        {
            Exception failure = (Exception)Activator.CreateInstance(type)!;
            Assert.True(ZabbixFrames.IsExchangeFailure(failure), $"{type.Name} is IsExchangeFailure's set but DeliverAsync's inline filter must match it too (kept equal by hand; this test is the pin)");
        }
    }

    [Fact]
    public async Task A_failed_response_is_a_sender_exception_and_the_batch_is_rejected_not_delivered()
    {
        var (port, request) = MockTrapper.Answer("""{"response":"failed","info":"cannot process request"}""");
        var logs = new FakeLogCollector();
        using var sender = new ZabbixSender([new ZabbixServerNode("127.0.0.1", port)], null, null, new FakeLogger<ZabbixSender>(logs));

        ZabbixDelivery delivery = await sender.DeliverAsync([new ZabbixValue("h", "k", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);

        Assert.Equal(ZabbixDelivery.Rejected, delivery);
        Assert.Contains("sender data", await request, StringComparison.Ordinal);
        FakeLogRecord refused = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 1007);
        Assert.Equal(LogLevel.Warning, refused.Level);
    }

    [Theory]
    [InlineData("data collection has been paused")]                                            // sender data (proxy.c 8.0:1818)
    [InlineData("data collection is paused")]                                                  // agent data (trapper.c 8.0:195)
    public async Task A_trapper_that_paused_data_collection_took_nothing_so_the_batch_is_held_and_retried(string info)
    {
        // The capped server takes nothing and says so, so the batch is held for a retry; Rejected would drop it for good.
        using var sender = new ZabbixSender((_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes($$"""{"response":"failed","info":"{{info}}"}""")), "scripted");

        ZabbixTrapperRefusedException refused = await Assert.ThrowsAsync<ZabbixTrapperRefusedException>(() => sender.SendAsync([new ZabbixValue("h", "k", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken));

        Assert.True(refused.IsPaused);
        Assert.Equal(ZabbixDelivery.Retried, await sender.DeliverAsync([new ZabbixValue("h", "k", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_failed_response_throws_with_the_servers_words()
    {
        var (port, _) = MockTrapper.Answer("""{"response":"failed","info":"cannot process request"}""");

        ZabbixTrapperRefusedException refused = await Assert.ThrowsAsync<ZabbixTrapperRefusedException>(async () => await new ZabbixSender("127.0.0.1", port).SendAsync([new ZabbixValue("h", "k", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken));

        Assert.Equal(("failed", "cannot process request"), (refused.Response, refused.Info));
        Assert.Equal("refused", Zabbix.Diagnostics.ZabbixErrorType.Of(refused));
    }

    [Theory]
    [InlineData("""{"response":"success"}""")]
    [InlineData("""{"response":"success","info":""}""")]
    [InlineData("""{"response":"success","info":"processed 1 failed 0"}""")]
    [InlineData("""{"response":"success","info":"Processed: 1; Failed: 0; Total: 1; Seconds spent: 0.1"}""")]
    public async Task An_info_line_that_is_not_upstreams_counters_is_refused_rather_than_read_as_zero(string reply)
    {
        var (port, _) = MockTrapper.Answer(reply);

        await Assert.ThrowsAsync<ZabbixTrapperRefusedException>(async () => await new ZabbixSender("127.0.0.1", port).SendAsync([new ZabbixValue("h", "k", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_reply_that_is_not_json_is_invalid_data()
    {
        var (port, _) = MockTrapper.Answer("not json");

        await Assert.ThrowsAsync<InvalidDataException>(async () => await new ZabbixSender("127.0.0.1", port).SendAsync([new ZabbixValue("h", "k", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Some_refused_values_inside_a_success_are_a_partial_response_not_a_failure()
    {
        var (port, _) = MockTrapper.Answer(MockTrapper.Success(processed: 1, failed: 1));

        using ZabbixSender sender2 = new ZabbixSender("127.0.0.1", port);
        ZabbixSenderResponse response = await sender2.SendAsync([new ZabbixValue("h", "k", "1", DateTimeOffset.UnixEpoch), new ZabbixValue("h", "nope", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);

        Assert.True(response.HasFailures);
        Assert.Equal((1, 1, 2), (response.Processed, response.Failed, response.Total));
    }
}

internal static class Lab
{
    /// <summary>
    /// A PostgreSQL connection string for the durable-store tests, from <c>ZABBIX_TEST_POSTGRES</c> — a database the tests
    /// own, never a Zabbix database; <see langword="null"/> when unset, and those tests skip.
    /// </summary>
    public static string? Postgres { get; } = Environment.GetEnvironmentVariable("ZABBIX_TEST_POSTGRES");
}
