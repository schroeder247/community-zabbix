// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The framed exchange as upstream's <c>commshigh.c</c> runs it: failover only at connect or handshake, never a re-send
/// after the write, a sticky node that moves past a failure, the deadline as a <see cref="TimeoutException"/>, the
/// heartbeat as a notification, and the wire's spans, durations and counters.
/// </summary>
public sealed class FramesTests
{
    private static readonly ZabbixValue Value = new("h", "k", "1", DateTimeOffset.UnixEpoch);

    /// <summary>A peer that takes each connection, reads one frame into <see cref="Frames"/>, and then does what it is told.</summary>
    internal sealed class Peer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _accepting;
        private readonly CancellationTokenSource _stop = new();

        public Peer(Func<NetworkStream, ReadOnlyMemory<byte>, CancellationToken, Task> then)
        {
            _listener.Start();
            _accepting = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch (OperationCanceledException) { return; }
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            NetworkStream stream = client.GetStream();
                            try
                            {
                                ReadOnlyMemory<byte> frame = await ZabbixHeader.ReadAsync(stream, _stop.Token);
                                await Frames.Writer.WriteAsync(Encoding.UTF8.GetString(frame.Span));
                                await then(stream, frame, _stop.Token);
                            }
                            catch (OperationCanceledException) { }
                            catch (ObjectDisposedException) { }
                            catch (IOException) { }
                            catch (InvalidDataException) { }
                        }
                    });
                }
            });
        }

        public Channel<string> Frames { get; } = Channel.CreateUnbounded<string>();

        public ZabbixServerNode Node => new("127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port);

        /// <summary>Resets the connection after reading the frame: what a standby HA node behind a port forward does.</summary>
        public static Task Reset(NetworkStream stream, ReadOnlyMemory<byte> frame, CancellationToken stopping)
        {
            stream.Socket.LingerState = new LingerOption(true, 0);
            stream.Socket.Close();
            return Task.CompletedTask;
        }

        public static Task Answer(NetworkStream stream, ReadOnlyMemory<byte> frame, CancellationToken stopping) => ZabbixHeader.WriteAsync(stream, Encoding.UTF8.GetBytes(MockTrapper.Success(1, 0)), stopping);

        /// <summary>Closes the connection after reading the frame, without a word: what a trapper does after a heartbeat it took.</summary>
        public static Task Close(NetworkStream stream, ReadOnlyMemory<byte> frame, CancellationToken stopping) => Task.CompletedTask;

        /// <summary>Holds the connection open and never answers, until the peer is disposed.</summary>
        public static Task Stall(NetworkStream stream, ReadOnlyMemory<byte> frame, CancellationToken stopping) => new TaskCompletionSource().Task.WaitAsync(stopping);

        // The accept loop ends on the cancellation alone; the listener is stopped only after the loop returned, so an
        // accept that a disposal overtakes never meets a disposed socket (ObjectDisposedException out of the loop).
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _accepting;
            _listener.Dispose();
            _stop.Dispose();
        }
    }

    internal static int ClosedPort()
    {
        using var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        int port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        return port;
    }

    [Fact]
    public async Task A_node_that_fails_after_the_write_is_not_sent_again_and_the_next_call_starts_at_the_next_node()
    {
        await using var resetting = new Peer(Peer.Reset);
        await using var answering = new Peer(Peer.Answer);
        var logs = new FakeLogCollector();
        using var sender = new ZabbixSender([resetting.Node, answering.Node], new ZabbixConnectionOptions { Timeout = TimeSpan.FromSeconds(30) }, null, new FakeLogger<ZabbixSender>(logs));

        Exception first = await Assert.ThrowsAnyAsync<Exception>(async () => await sender.SendAsync([Value], TestContext.Current.CancellationToken));
        ZabbixSenderResponse second = await sender.SendAsync([Value], TestContext.Current.CancellationToken);

        Assert.True(ZabbixFrames.IsExchangeFailure(first), first.GetType().Name);
        Assert.Equal(1, resetting.Frames.Reader.Count);                                   // written once, never re-sent
        Assert.Equal(1, answering.Frames.Reader.Count);                                   // only the second call reached it
        Assert.Equal(1, second.Processed);
        FakeLogRecord failed = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 1106);
        Assert.Equal(LogLevel.Debug, failed.Level);
        FakeLogRecord reported = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 1003);   // one failure, logged once
        Assert.Equal(LogLevel.Debug, reported.Level);                                    // the caller has the exception
        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Level >= LogLevel.Warning);
        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Id.Id == 1102);        // no failover after a write
    }

    [Fact]
    public async Task A_node_that_cannot_be_connected_fails_over_within_the_call_and_the_answering_node_stays_first()
    {
        await using var answering = new Peer(Peer.Answer);
        var logs = new FakeLogCollector();
        using var sender = new ZabbixSender([new ZabbixServerNode("127.0.0.1", ClosedPort()), answering.Node], new ZabbixConnectionOptions { Timeout = TimeSpan.FromSeconds(30) }, null, new FakeLogger<ZabbixSender>(logs));

        await sender.SendAsync([Value], TestContext.Current.CancellationToken);
        await sender.SendAsync([Value], TestContext.Current.CancellationToken);

        Assert.Equal(2, answering.Frames.Reader.Count);
        FakeLogRecord failover = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 1102);   // the refused node was tried once
        Assert.Equal(LogLevel.Warning, failover.Level);
        Assert.Contains("connection_refused", failover.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_no_node_can_be_connected_the_last_nodes_failure_is_thrown()
    {
        int port = ClosedPort();
        using var sender = new ZabbixSender([new ZabbixServerNode("127.0.0.1", port), new ZabbixServerNode("127.0.0.1", port)], new ZabbixConnectionOptions { Timeout = TimeSpan.FromSeconds(30) });

        await Assert.ThrowsAsync<SocketException>(async () => await sender.SendAsync([Value], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_deadline_is_a_timeout_exception_and_is_logged()
    {
        await using var stalled = new Peer(Peer.Stall);
        var time = new FakeTimeProvider();
        var logs = new FakeLogCollector();
        ZabbixExchangeCallback exchange = ZabbixFrames.Connect(stalled.Node, new ZabbixConnectionOptions { Timeout = TimeSpan.FromSeconds(5) }, time, new FakeLogger(logs));

        ValueTask<ReadOnlyMemory<byte>> pending = exchange("{}"u8.ToArray(), TestContext.Current.CancellationToken);
        await stalled.Frames.Reader.ReadAsync(TestContext.Current.CancellationToken);                                          // the request is written and waiting
        time.Advance(TimeSpan.FromSeconds(5));

        TimeoutException timeout = await Assert.ThrowsAsync<TimeoutException>(async () => await pending);
        Assert.Contains(stalled.Node.ToString(), timeout.Message, StringComparison.Ordinal);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1105 && record.Level == LogLevel.Debug);
    }

    /// <summary>A raw listener that accepts and reads the first byte of a handshake, then never answers: the connect phase
    /// (TCP done, TLS never starting to reply) is what stalls, so the deadline is spent inside <c>ConnectAsync</c> and
    /// makes the node fail over, unlike a stall of the round trip after a node is already connected.</summary>
    private sealed class HandshakeStallListener : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource _began = new();
        private readonly TaskCompletionSource _release = new();
        private readonly Task _accepting;

        public HandshakeStallListener()
        {
            _listener.Start();
            _accepting = Task.Run(async () =>
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync();
                _ = await client.GetStream().ReadAsync(new byte[1]);              // the ClientHello began; never answer it
                _began.SetResult();
                await _release.Task;
            });
        }

        public ZabbixServerNode Node => new("127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port);

        public Task Began => _began.Task;

        public async ValueTask DisposeAsync()
        {
            _release.TrySetResult();
            await _accepting;
            _listener.Dispose();
        }
    }

    [Fact]
    public async Task A_failover_over_two_unreachable_nodes_costs_two_timeouts_not_one()
    {
        await using var first = new HandshakeStallListener();
        await using var second = new HandshakeStallListener();
        var time = new FakeTimeProvider();
        var options = new ZabbixConnectionOptions
        {
            Timeout = TimeSpan.FromSeconds(5),
            Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = "edge-01", Psk = "1f87b595725ac58dd977beef14b97461a7c1045b9a1c963065002c5473194952" },
        };
        ZabbixExchangeCallback exchange = ZabbixFrames.Over([first.Node, second.Node], options, time);

        ValueTask<ReadOnlyMemory<byte>> pending = exchange("{}"u8.ToArray(), TestContext.Current.CancellationToken);
        await first.Began;                                                        // node 1's attempt is under way
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(5));                                     // node 1's own deadline, not the whole exchange's

        await second.Began;                                                        // failover moved to node 2, its own fresh deadline
        Assert.False(pending.IsCompleted);                                         // one Timeout was not enough
        time.Advance(TimeSpan.FromSeconds(5));                                     // node 2's own deadline

        await Assert.ThrowsAsync<TimeoutException>(async () => await pending);     // two Timeouts, not one, reached the caller
    }

    [Fact]
    public async Task The_callers_cancellation_stays_an_operation_cancelled_exception()
    {
        await using var stalled = new Peer(Peer.Stall);
        using var cancel = new CancellationTokenSource();
        ZabbixExchangeCallback exchange = ZabbixFrames.Connect(stalled.Node, new ZabbixConnectionOptions { Timeout = TimeSpan.FromMinutes(5) });

        ValueTask<ReadOnlyMemory<byte>> pending = exchange("{}"u8.ToArray(), cancel.Token);
        await stalled.Frames.Reader.ReadAsync(TestContext.Current.CancellationToken);
        await cancel.CancelAsync();

        Exception stopped = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.IsNotType<TimeoutException>(stopped);
    }

    [Fact]
    public async Task A_notification_listens_until_the_peer_closes_and_a_peer_that_holds_the_connection_costs_the_deadline_not_a_failure()
    {
        await using var silent = new Peer(Peer.Stall);                                     // never answers, never closes
        var time = new FakeTimeProvider();
        ZabbixNotificationCallback notify = ZabbixFrames.Notify([silent.Node], new ZabbixConnectionOptions { Timeout = TimeSpan.FromSeconds(5) }, time);

        ValueTask pending = notify("""{"request":"active check heartbeat"}"""u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal("""{"request":"active check heartbeat"}""", await silent.Frames.Reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.False(pending.IsCompleted);                                                // upstream reads for a redirect reply
        time.Advance(TimeSpan.FromSeconds(5));

        await pending;                                                                   // the deadline ends the listen: success
    }

    [Fact]
    public async Task A_notification_whose_peer_closes_without_a_word_completes()
    {
        await using var closing = new Peer(Peer.Close);
        ZabbixNotificationCallback notify = ZabbixFrames.Notify([closing.Node], new ZabbixConnectionOptions { Timeout = TimeSpan.FromMinutes(5) });

        await notify("{}"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(1, closing.Frames.Reader.Count);
    }

    [Fact]
    public async Task A_notification_answered_with_something_other_than_a_redirect_is_ignored()
    {
        await using var chatty = new Peer((stream, frame, stopping) => ZabbixHeader.WriteAsync(stream, """{"response":"failed","info":"host not found"}"""u8.ToArray(), stopping));
        ZabbixNotificationCallback notify = ZabbixFrames.Notify([chatty.Node], new ZabbixConnectionOptions { Timeout = TimeSpan.FromMinutes(5) });

        await notify("{}"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(1, chatty.Frames.Reader.Count);
    }

    [Fact]
    public async Task A_reply_that_is_not_a_frame_is_refused_and_logged()
    {
        await using var http = new Peer((stream, frame, stopping) => stream.WriteAsync("HTTP/1.1 400 Bad Request\r\n\r\n"u8.ToArray(), stopping).AsTask());
        var logs = new FakeLogCollector();
        ZabbixExchangeCallback exchange = ZabbixFrames.Connect(http.Node, logger: new FakeLogger(logs));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await exchange("{}"u8.ToArray(), TestContext.Current.CancellationToken));

        FakeLogRecord refused = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 1104);
        Assert.Equal(LogLevel.Debug, refused.Level);                                          // the façade reports the failure; this says why
        Assert.Contains("ZBXD", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_exchange_with_a_timeout_that_is_not_positive_is_refused_when_it_is_built(int seconds)
        => Assert.Throws<ArgumentException>(() => ZabbixFrames.Over([new ZabbixServerNode("127.0.0.1")], new ZabbixConnectionOptions { Timeout = TimeSpan.FromSeconds(seconds) }));

    [Fact]
    public void An_exchange_over_a_default_node_is_refused_when_it_is_built()
        => Assert.Throws<ArgumentException>(() => ZabbixFrames.Over([default]));

    // ----- telemetry ------------------------------------------------------------------------------------------

    /// <summary>The wire's spans in this test's trace (the source is static and shared by parallel tests) and its instruments on a meter factory of the test's own.</summary>
    internal sealed class Capture : IDisposable
    {
        private static readonly ActivitySource Tests = new("Community.Zabbix.Tests.WireCapture");
        private readonly ActivityListener _listener;
        private readonly Activity _root;
        private readonly ConcurrentQueue<Activity> _spans = new();
        private readonly ServiceProvider _provider;

        public Capture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name is ZabbixTelemetryNames.Wire or "Community.Zabbix.Tests.WireCapture",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _spans.Enqueue(activity),
            };
            ActivitySource.AddActivityListener(_listener);
            _root = Tests.StartActivity("test")!;
            _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            IMeterFactory meters = _provider.GetRequiredService<IMeterFactory>();
            Telemetry = new ZabbixWireTelemetry(meters);
            Durations = new MetricCollector<double>(meters, ZabbixTelemetryNames.Wire, ZabbixWireTelemetry.DurationInstrument);
            Failovers = new MetricCollector<long>(meters, ZabbixTelemetryNames.Wire, ZabbixWireTelemetry.FailoverInstrument);
            Redirects = new MetricCollector<long>(meters, ZabbixTelemetryNames.Wire, ZabbixWireTelemetry.RedirectInstrument);
        }

        public ZabbixWireTelemetry Telemetry { get; }

        public MetricCollector<double> Durations { get; }

        public MetricCollector<long> Failovers { get; }

        public MetricCollector<long> Redirects { get; }

        public IReadOnlyList<Activity> Spans => [.. _spans.Where(span => span.Source.Name == ZabbixTelemetryNames.Wire && span.TraceId == _root.TraceId)];

        public void Dispose()
        {
            _root.Dispose();
            _listener.Dispose();
            Durations.Dispose();
            Failovers.Dispose();
            Redirects.Dispose();
            Telemetry.Dispose();
            _provider.Dispose();
        }
    }

    [Fact]
    public async Task An_exchange_leaves_a_client_span_and_a_duration_in_seconds_with_the_peer()
    {
        await using var answering = new Peer(Peer.Answer);
        using var capture = new Capture();
        ZabbixExchangeCallback exchange = ZabbixFrames.Connect(answering.Node, telemetry: capture.Telemetry);

        await exchange("{}"u8.ToArray(), TestContext.Current.CancellationToken);

        Activity span = Assert.Single(capture.Spans);
        Assert.Equal((ZabbixWireTelemetry.ExchangeSpan, ActivityKind.Client, ActivityStatusCode.Ok), (span.OperationName, span.Kind, span.Status));
        Assert.Equal(("127.0.0.1", (object)answering.Node.Port, "tcp", "unencrypted"),
            (span.GetTagItem("server.address"), span.GetTagItem("server.port"), span.GetTagItem("network.transport"), span.GetTagItem("zabbix.tls.mode")));
        CollectedMeasurement<double> duration = Assert.Single(capture.Durations.GetMeasurementSnapshot());
        Assert.Equal(answering.Node.Port, duration.Tags["server.port"]);
        Assert.False(duration.Tags.ContainsKey("error.type"));
        Assert.Equal("s", capture.Durations.Instrument!.Unit);
    }

    [Fact]
    public async Task A_failed_exchange_carries_error_type_on_the_span_and_the_duration()
    {
        using var capture = new Capture();
        ZabbixExchangeCallback exchange = ZabbixFrames.Connect(new ZabbixServerNode("127.0.0.1", ClosedPort()), telemetry: capture.Telemetry);

        await Assert.ThrowsAsync<SocketException>(async () => await exchange("{}"u8.ToArray(), TestContext.Current.CancellationToken));

        Activity span = Assert.Single(capture.Spans);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("connection_refused", span.GetTagItem("error.type"));
        Assert.Equal("connection_refused", Assert.Single(capture.Durations.GetMeasurementSnapshot()).Tags["error.type"]);
    }

    [Fact]
    public async Task A_failover_is_counted_and_is_an_event_on_the_span()
    {
        await using var answering = new Peer(Peer.Answer);
        using var capture = new Capture();
        int refused = ClosedPort();
        ZabbixExchangeCallback exchange = ZabbixFrames.Over([new ZabbixServerNode("127.0.0.1", refused), answering.Node], telemetry: capture.Telemetry);

        await exchange("{}"u8.ToArray(), TestContext.Current.CancellationToken);

        CollectedMeasurement<long> failover = Assert.Single(capture.Failovers.GetMeasurementSnapshot());
        Assert.Equal((1L, (object?)refused), (failover.Value, failover.Tags["server.port"]));
        Activity span = Assert.Single(capture.Spans);
        Assert.Contains(span.Events, e => e.Name == ZabbixWireTelemetry.FailoverEvent);
        Assert.Equal(answering.Node.Port, span.GetTagItem("server.port"));
    }

    [Fact]
    public async Task A_notification_leaves_its_own_span()
    {
        await using var peer = new Peer(Peer.Close);
        using var capture = new Capture();
        ZabbixNotificationCallback notify = ZabbixFrames.Notify([peer.Node], telemetry: capture.Telemetry);

        await notify("{}"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(ZabbixWireTelemetry.NotifySpan, Assert.Single(capture.Spans).OperationName);
    }

    // A frame header that declares 32 MiB and sends nothing of it: a peer that asks for twice the default reply cap.
    private static async Task DeclareThirtyTwoMegabytes(NetworkStream stream, ReadOnlyMemory<byte> frame, CancellationToken stopping)
    {
        byte[] header = [.. "ZBXD"u8, 0x01, .. BitConverter.GetBytes(32 * 1024 * 1024), .. BitConverter.GetBytes(0)];
        await stream.WriteAsync(header, stopping);
        await stream.FlushAsync(stopping);
    }

    [Fact]
    public async Task A_reply_is_capped_at_sixteen_megabytes_unless_the_connection_says_otherwise()
    {
        await using var greedy = new Peer(DeclareThirtyTwoMegabytes);
        using var capped = new ZabbixSender([greedy.Node]);
        using var opened = new ZabbixSender([greedy.Node], new ZabbixConnectionOptions { MaxPayloadBytes = 1L << 30 });

        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() => capped.SendAsync([Value], TestContext.Current.CancellationToken));
        InvalidDataException cut = await Assert.ThrowsAsync<InvalidDataException>(() => opened.SendAsync([Value], TestContext.Current.CancellationToken));

        Assert.Equal($"The frame declares {32 * 1024 * 1024} bytes; the cap is {ZabbixConnectionOptions.DefaultMaxPayloadBytes}.", refused.Message);
        Assert.Equal("The connection ended before a whole frame arrived.", cut.Message);   // the size was allowed; the body never came
    }

    [Fact]
    public void A_reply_s_cap_is_the_connection_s_or_sixteen_megabytes_and_a_large_frame_keeps_upstream_s()
    {
        Assert.Equal(16L * 1024 * 1024, new ZabbixConnectionOptions().ReplyCap(allowLarge: false));
        Assert.Null(new ZabbixConnectionOptions().ReplyCap(allowLarge: true));                  // a proxy's configuration: ZBX_MAX_RECV_LARGE_DATA_SIZE
        Assert.Equal(4096, new ZabbixConnectionOptions { MaxPayloadBytes = 4096 }.ReplyCap(allowLarge: true));
    }

    [Fact]
    public async Task A_peer_that_closes_before_a_frame_is_the_try_read_s_null_and_the_read_s_no_reply()
    {
        var pipe = new System.IO.Pipelines.Pipe();
        await pipe.Writer.CompleteAsync();

        Assert.Null(await ZabbixHeader.TryReadAsync(pipe.Reader, null, false, TestContext.Current.CancellationToken));
        var again = new System.IO.Pipelines.Pipe();
        await again.Writer.CompleteAsync();
        InvalidDataException closed = await Assert.ThrowsAsync<InvalidDataException>(async () => await ZabbixHeader.ReadAsync(again.Reader, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(ZabbixHeader.IsNoReply(closed));
    }

    [Fact]
    public async Task A_large_frame_is_read_from_its_stream_in_large_reads_not_one_per_pipe_segment()
    {
        byte[] body = new byte[1024 * 1024];
        using var wire = new MemoryStream();
        await ZabbixHeader.WriteAsync(wire, body, TestContext.Current.CancellationToken);
        wire.Position = 0;
        using var counting = new CountingStream(wire);

        ReadOnlyMemory<byte> read = await ZabbixHeader.ReadAsync(counting, TestContext.Current.CancellationToken);

        Assert.Equal(body.Length, read.Length);
        Assert.True(counting.Reads <= 32, $"{counting.Reads} reads for a 1 MiB frame");      // 4 KiB pipe segments would take 256
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public int Reads { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return inner.Read(buffer, offset, count);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
