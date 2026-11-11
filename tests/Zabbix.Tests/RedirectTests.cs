// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Hosting;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// A proxy group's redirects, against fakes that answer as upstream does: the trapper's and the heartbeat's
/// refusal is <c>zbx_send_response_json</c> over <c>zbx_add_redirect_response</c> — <c>{"redirect":{"revision":N,
/// "address":"host:port"},"response":"failed"}</c>, or <c>{"redirect":{"reset":true},"response":"failed"}</c>
/// (<c>proxy.c</c>, <c>trapper.c</c>, <c>commshigh.c</c>, 7.0–8.0) — and the client side is
/// <c>zbx_comms_exchange_with_redirect</c>'s.
/// </summary>
public sealed class RedirectTests
{
    private static readonly ZabbixValue Value = new("h", "k", "1", DateTimeOffset.UnixEpoch);

    private static byte[] RedirectTo(ZabbixServerNode target, ulong revision)
        => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $$"""{"redirect":{"revision":{{revision}},"address":"{{target}}"},"response":"failed"}"""));

    private static readonly byte[] Reset = """{"redirect":{"reset":true},"response":"failed"}"""u8.ToArray();

    private static FramesTests.Peer Answering(Func<int, byte[]> reply)
    {
        int calls = 0;
        return new FramesTests.Peer((stream, frame, stopping) => ZabbixHeader.WriteAsync(stream, reply(Interlocked.Increment(ref calls)), stopping));
    }

    private static FramesTests.Peer Success() => Answering(_ => Encoding.UTF8.GetBytes(MockTrapper.Success(1, 0)));

    // ----- the reply's grammar -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"redirect":{"revision":3,"address":"10.0.0.9:10053"},"response":"failed"}""", "10.0.0.9", 10053, 3UL)]
    [InlineData("""{"response":"failed","redirect":{"address":"proxy-b","revision":"7"}}""", "proxy-b", 10051, 7UL)]
    [InlineData("""{"response":"failed","redirect":{"revision":1,"address":"[fe80::1]:10060"}}""", "fe80::1", 10060, 1UL)]
    public void A_redirect_reply_is_read_as_upstream_parses_it(string reply, string host, int port, ulong revision)
    {
        Assert.True(ZabbixRedirect.TryParse(Encoding.UTF8.GetBytes(reply), out ZabbixRedirect? redirect));
        Assert.Equal(new ZabbixRedirect(new ZabbixServerNode(host, port), revision, Reset: false), redirect);
    }

    [Theory]
    [InlineData("""{"redirect":{"reset":true},"response":"failed"}""")]
    [InlineData("""{"response":"failed","redirect":{"reset":"true","revision":9}}""")]
    public void A_reset_wins_over_everything_else_in_the_block(string reply)
    {
        Assert.True(ZabbixRedirect.TryParse(Encoding.UTF8.GetBytes(reply), out ZabbixRedirect? redirect));
        Assert.True(redirect.Reset);
        Assert.Null(redirect.Address);
    }

    [Theory]
    [InlineData("""{"response":"success","redirect":{"revision":3,"address":"a:1"}}""")]         // only a failure redirects
    [InlineData("""{"response":"failed","info":"host not found"}""")]                          // a plain refusal
    [InlineData("""{"response":"failed","redirect":{"address":"a:1"}}""")]                    // no revision
    [InlineData("""{"response":"failed","redirect":{"revision":-1,"address":"a:1"}}""")]      // not unsigned
    [InlineData("""{"response":"failed","redirect":{"revision":3}}""")]                       // no address
    [InlineData("""{"response":"failed","redirect":{"revision":3,"address":"a b"}}""")]       // not a ServerActive element
    [InlineData("""{"redirect":{"revision":3,"address":"a:1"}}""")]                            // no response
    [InlineData("processed: 1")]                                                              // not JSON
    public void A_reply_that_is_not_a_valid_redirect_is_the_callers(string reply)
        => Assert.False(ZabbixRedirect.TryParse(Encoding.UTF8.GetBytes(reply), out _));

    // ----- following -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_redirect_sends_the_request_to_its_target_once_and_the_target_stays_first()
    {
        await using var target = Success();
        await using var moved = Answering(_ => RedirectTo(target.Node, 1));
        var logs = new FakeLogCollector();
        using var sender = new ZabbixSender([moved.Node], null, null, new FakeLogger<ZabbixSender>(logs));

        ZabbixSenderResponse first = await sender.SendAsync([Value], TestContext.Current.CancellationToken);
        ZabbixSenderResponse second = await sender.SendAsync([Value], TestContext.Current.CancellationToken);

        Assert.Equal((1, 1), (first.Processed, second.Processed));
        Assert.Equal(1, moved.Frames.Reader.Count);                                       // asked once; the redirect then stays first
        Assert.Equal(2, target.Frames.Reader.Count);
        FakeLogRecord redirected = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 1109);
        Assert.Equal(LogLevel.Information, redirected.Level);
        Assert.Contains(target.Node.ToString(), redirected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_target_that_cannot_be_connected_fails_the_exchange_without_failover_and_the_next_call_walks_the_list()
    {
        int closed = FramesTests.ClosedPort();
        await using var moved = Answering(call => call == 1 ? RedirectTo(new ZabbixServerNode("127.0.0.1", closed), 1) : Encoding.UTF8.GetBytes(MockTrapper.Success(1, 0)));
        using var sender = new ZabbixSender([moved.Node]);

        await Assert.ThrowsAsync<SocketException>(async () => await sender.SendAsync([Value], TestContext.Current.CancellationToken));   // "redirect connection without failover"
        ZabbixSenderResponse again = await sender.SendAsync([Value], TestContext.Current.CancellationToken);                                     // the dead target fails over to the configured node

        Assert.Equal(1, again.Processed);
        Assert.Equal(2, moved.Frames.Reader.Count);
    }

    [Fact]
    public async Task A_reset_moves_the_answering_node_behind_the_others_and_sends_again_with_failover()
    {
        await using var lost = Answering(_ => Reset);
        await using var other = Success();
        ZabbixExchangeCallback exchange = ZabbixFrames.Over([lost.Node, other.Node], followRedirects: true);

        await exchange("{}"u8.ToArray(), TestContext.Current.CancellationToken);
        await exchange("{}"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(1, lost.Frames.Reader.Count);                                        // the reset node went behind
        Assert.Equal(2, other.Frames.Reader.Count);
    }

    [Fact]
    public async Task A_second_redirect_in_one_exchange_fails_it_as_sequential_and_nothing_goes_further()
    {
        await using var third = Success();
        await using var second = Answering(_ => RedirectTo(third.Node, 2));
        await using var first = Answering(_ => RedirectTo(second.Node, 1));
        var logs = new FakeLogCollector();
        ZabbixExchangeCallback exchange = ZabbixFrames.Over([first.Node], logger: new FakeLogger(logs), followRedirects: true);

        IOException failed = await Assert.ThrowsAnyAsync<IOException>(async () => await exchange("{}"u8.ToArray(), TestContext.Current.CancellationToken));

        Assert.Contains("sequential redirect responses detected", failed.Message, StringComparison.Ordinal);
        Assert.Equal("redirect", ZabbixErrorType.Of(failed));
        Assert.Equal(0, third.Frames.Reader.Count);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1111 && record.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_redirect_older_than_the_one_followed_is_not_followed()
    {
        await using var elsewhere = Success();
        await using var target = Answering(call => call == 1 ? Encoding.UTF8.GetBytes(MockTrapper.Success(1, 0)) : RedirectTo(elsewhere.Node, 3));
        await using var moved = Answering(_ => RedirectTo(target.Node, 5));
        ZabbixExchangeCallback exchange = ZabbixFrames.Over([moved.Node], followRedirects: true);

        await exchange("{}"u8.ToArray(), TestContext.Current.CancellationToken);                                        // revision 5 followed
        IOException stale = await Assert.ThrowsAnyAsync<IOException>(async () => await exchange("{}"u8.ToArray(), TestContext.Current.CancellationToken));

        Assert.Contains("connection was reset because of service being offline", stale.Message, StringComparison.Ordinal);
        Assert.Equal(0, elsewhere.Frames.Reader.Count);
    }

    [Fact]
    public async Task A_newer_redirect_replaces_the_one_followed()
    {
        await using var latest = Success();
        await using var target = Answering(call => call == 1 ? Encoding.UTF8.GetBytes(MockTrapper.Success(1, 0)) : RedirectTo(latest.Node, 6));
        await using var moved = Answering(_ => RedirectTo(target.Node, 5));
        ZabbixExchangeCallback exchange = ZabbixFrames.Over([moved.Node], followRedirects: true);

        await exchange("{}"u8.ToArray(), TestContext.Current.CancellationToken);
        await exchange("{}"u8.ToArray(), TestContext.Current.CancellationToken);
        await exchange("{}"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(2, latest.Frames.Reader.Count);                                      // revision 6 replaced revision 5 and stays first
        Assert.Equal(2, target.Frames.Reader.Count);
        Assert.Equal(1, moved.Frames.Reader.Count);
    }

    [Fact]
    public async Task An_exchange_that_does_not_follow_redirects_hands_the_reply_back_and_the_sender_keeps_the_batch()
    {
        var (port, _) = MockTrapper.Answer("""{"redirect":{"revision":3,"address":"10.0.0.9:10053"},"response":"failed"}""");
        var logs = new FakeLogCollector();
        using var sender = new ZabbixSender(ZabbixFrames.Connect(new ZabbixServerNode("127.0.0.1", port)), "proxy-a", new FakeLogger<ZabbixSender>(logs));

        ZabbixDelivery delivery = await sender.DeliverAsync([Value], TestContext.Current.CancellationToken);

        Assert.Equal(ZabbixDelivery.Retried, delivery);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1008 && record.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_redirect_the_sender_cannot_follow_is_an_io_failure_typed_redirect()
    {
        var (port, _) = MockTrapper.Answer("""{"redirect":{"reset":true},"response":"failed"}""");
        using var sender = new ZabbixSender(ZabbixFrames.Connect(new ZabbixServerNode("127.0.0.1", port)), "proxy-a");

        IOException failed = await Assert.ThrowsAnyAsync<IOException>(async () => await sender.SendAsync([Value], TestContext.Current.CancellationToken));

        Assert.Equal("redirect", ZabbixErrorType.Of(failed));
    }

    [Fact]
    public async Task A_heartbeat_answered_with_a_redirect_is_sent_again_to_the_target_which_stays_first()
    {
        await using var target = new FramesTests.Peer(FramesTests.Peer.Close);
        await using var moved = Answering(_ => RedirectTo(target.Node, 1));
        ZabbixNotificationCallback notify = ZabbixFrames.Notify([moved.Node], new ZabbixConnectionOptions { Timeout = TimeSpan.FromMinutes(5) });

        await notify("""{"request":"active check heartbeat"}"""u8.ToArray(), TestContext.Current.CancellationToken);
        await notify("""{"request":"active check heartbeat"}"""u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(1, moved.Frames.Reader.Count);
        Assert.Equal(2, target.Frames.Reader.Count);
    }

    [Fact]
    public async Task A_heartbeat_whose_peer_closes_without_a_redirect_logs_the_reason_at_debug_and_returns()
    {
        var logs = new FakeLogCollector();
        await using var closes = new FramesTests.Peer(FramesTests.Peer.Close);
        ZabbixNotificationCallback notify = ZabbixFrames.Notify([closes.Node], logger: new FakeLogger(logs));

        await notify("""{"request":"active check heartbeat"}"""u8.ToArray(), TestContext.Current.CancellationToken);

        FakeLogRecord record = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 1112);
        Assert.Equal(LogLevel.Debug, record.Level);
    }

    [Fact]
    public async Task A_followed_redirect_is_counted_and_is_an_event_on_the_span()
    {
        await using var target = Success();
        await using var moved = Answering(_ => RedirectTo(target.Node, 1));
        using var capture = new FramesTests.Capture();
        ZabbixExchangeCallback exchange = ZabbixFrames.Over([moved.Node], telemetry: capture.Telemetry, followRedirects: true);

        await exchange("{}"u8.ToArray(), TestContext.Current.CancellationToken);

        CollectedMeasurement<long> redirect = Assert.Single(capture.Redirects.GetMeasurementSnapshot());
        Assert.Equal((1L, (object?)moved.Node.Port, (object?)false), (redirect.Value, redirect.Tags["server.port"], redirect.Tags["zabbix.wire.redirect.reset"]));
        Activity span = Assert.Single(capture.Spans);
        Assert.Contains(span.Events, e => e.Name == ZabbixWireTelemetry.RedirectEvent);
        Assert.Equal(target.Node.Port, span.GetTagItem("server.port"));                  // the span ends at the node that answered
    }

    [Fact]
    public async Task An_active_agent_asks_the_proxy_its_item_list_was_redirected_to()
    {
        await using var target = Answering(_ => """{"response":"success","data":[{"itemid":"100","key":"agent.ping","delay":"1h"}],"config_revision":2}"""u8.ToArray());
        await using var moved = Answering(_ => RedirectTo(target.Node, 4));
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddZabbixActiveAgent("moved").AddServer(new DnsEndPoint(moved.Node.Host, moved.Node.Port))
            .Configure(options => { options.HeartbeatInterval = TimeSpan.Zero; options.RefreshInterval = TimeSpan.FromDays(1); });
        await using Microsoft.Extensions.DependencyInjection.ServiceProvider provider = services.BuildValidated();
        ZabbixActiveAgent component = Microsoft.Extensions.DependencyInjection.ServiceProviderKeyedServiceExtensions.GetRequiredKeyedService<ZabbixActiveAgent>(provider, "moved");

        await component.HostStartingAsync(TestContext.Current.CancellationToken);
        Hosting.ZabbixActiveAgentDestination agent = component.Sessions[0];
        await agent.StoppedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(100UL, Assert.Single(agent.Assignments).Key);
        Assert.Equal(1, moved.Frames.Reader.Count);
        Assert.Equal(1, target.Frames.Reader.Count);
    }
}
