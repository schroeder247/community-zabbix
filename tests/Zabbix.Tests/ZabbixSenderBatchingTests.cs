// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using Community.Zabbix.Hosting;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The sender splits a call into frames as <c>zabbix_sender</c> does: at most <see cref="ZabbixSender.BatchSize"/> values
/// (250, upstream's <c>VALUES_MAX</c>) and <see cref="ZabbixSender.MaxBatchBytes"/> bytes a frame, sent in order, each when
/// the one before was answered. A call answers with the frames' replies summed; a frame that fails after others were taken
/// stops the call with what was taken and what was not confirmed, so a caller can send the rest without sending anything
/// twice. A send loop's batch is its own frame, whatever the sender's batch size.
/// </summary>
public sealed class ZabbixSenderBatchingTests
{
    [Fact]
    public async Task Ten_thousand_values_go_in_frames_of_250_as_zabbix_sender_sends_them()
    {
        (int port, Task<IReadOnlyList<byte[]>> frames) = MockTrapper.Record(40);
        using var sender = new ZabbixSender("127.0.0.1", port);

        ZabbixSenderResponse response = await sender.SendAsync(Values(10_000), TestContext.Current.CancellationToken);

        IReadOnlyList<byte[]> recorded = await frames.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.All(recorded, frame => Assert.Equal(250, MockTrapper.Values(frame)));
        Assert.Equal((10_000, 0, 10_000, 40), (response.Processed, response.Failed, response.Total, response.Frames));
        Assert.Matches(@"^processed: 10000; failed: 0; total: 10000; seconds spent: \d+\.\d{6}$", response.Info);
    }

    [Fact]
    public async Task A_batch_size_splits_the_values_into_full_frames_and_one_remainder_in_order()
    {
        (int port, Task<IReadOnlyList<byte[]>> frames) = MockTrapper.Record(3);
        using var sender = new ZabbixSender("127.0.0.1", port) { BatchSize = 3 };

        ZabbixSenderResponse response = await sender.SendAsync(Values(7), TestContext.Current.CancellationToken);

        IReadOnlyList<byte[]> recorded = await frames.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal([3, 3, 1], recorded.Select(MockTrapper.Values));
        Assert.Equal(["0", "1", "2", "3", "4", "5", "6"], recorded.SelectMany(ValuesOf));
        Assert.Equal((7, 3), (response.Total, response.Frames));
    }

    [Fact]
    public async Task One_frame_answers_with_the_trapper_s_own_reply()
    {
        (int port, Task<IReadOnlyList<byte[]>> frames) = MockTrapper.Record(1);
        using var sender = new ZabbixSender("127.0.0.1", port);

        ZabbixSenderResponse response = await sender.SendAsync(Values(250), TestContext.Current.CancellationToken);

        await frames.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal((250, 1, "processed: 250; failed: 0; total: 250; seconds spent: 0.000100"), (response.Total, response.Frames, response.Info));
    }

    [Fact]
    public async Task A_byte_limit_closes_a_frame_before_the_value_that_would_pass_it()
    {
        List<byte[]> sent = [];
        using var sender = new ZabbixSender(Recording(sent), "scripted") { MaxBatchBytes = 400 };

        await sender.SendAsync(Values(10, value: new string('x', 60)), TestContext.Current.CancellationToken);

        Assert.True(sent.Count > 1, $"{sent.Count} frame(s)");
        Assert.All(sent, frame => Assert.True(frame.Length <= 400, $"a frame of {frame.Length} bytes"));
        Assert.Equal(10, sent.Sum(MockTrapper.Values));
    }

    [Fact]
    public async Task A_value_that_alone_passes_the_byte_limit_is_refused_before_anything_is_sent()
    {
        List<byte[]> sent = [];
        using var sender = new ZabbixSender(Recording(sent), "scripted") { MaxBatchBytes = 200 };

        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync([.. Values(2), new ZabbixValue("web-01", "big", new string('x', 300), DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken));

        Assert.Equal("values", refused.ParamName);                                     // the overload the caller called
        Assert.Empty(sent);
    }

    [Fact]
    public async Task An_empty_call_is_one_empty_frame_and_its_envelope_is_the_size_the_byte_limit_counts()
    {
        List<byte[]> sent = [];
        using var sender = new ZabbixSender(Recording(sent), "scripted");

        await sender.SendAsync(Array.Empty<ZabbixSenderItem>(), TestContext.Current.CancellationToken);

        Assert.Equal("""{"request":"sender data","data":[]}""", System.Text.Encoding.UTF8.GetString(Assert.Single(sent)));
        Assert.Equal(35, sent[0].Length);
    }

    [Fact]
    public async Task A_frame_refused_after_two_were_taken_stops_the_call_with_what_was_taken_and_what_was_not_confirmed()
    {
        (int port, Task<IReadOnlyList<byte[]>> frames) = MockTrapper.Record(3, refuseAt: 2);
        var logs = new FakeLogCollector();
        using var sender = new ZabbixSender([new ZabbixServerNode("127.0.0.1", port)], null, null, new FakeLogger<ZabbixSender>(logs)) { BatchSize = 4 };
        IReadOnlyList<ZabbixValue> values = Values(14);

        ZabbixSenderIncompleteException stopped = await Assert.ThrowsAsync<ZabbixSenderIncompleteException>(() => sender.SendAsync(values, TestContext.Current.CancellationToken));

        Assert.Equal(3, (await frames.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Count);   // nothing after the refused frame
        Assert.Equal((2, 8, 8), (stopped.Delivered!.Frames, stopped.Delivered.Processed, stopped.Delivered.Total));
        Assert.Equal(values.Skip(8), stopped.Unconfirmed);
        Assert.IsType<ZabbixTrapperRefusedException>(stopped.InnerException);
        Assert.IsAssignableFrom<IOException>(stopped);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1009);
    }

    [Fact]
    public void A_usage_error_is_thrown_at_the_call_under_the_caller_s_own_parameter_name()
    {
        using var sender = new ZabbixSender(Recording([]), "scripted") { MaxBatchBytes = 200 };
        IReadOnlyList<ZabbixValue> values = [new ZabbixValue("web-01", "big", new string('x', 300), DateTimeOffset.UnixEpoch)];

        ArgumentException refused = Assert.IsType<ArgumentException>(Record.Exception(() => { _ = sender.SendAsync(values, TestContext.Current.CancellationToken); }));   // at the call, not a faulted task

        Assert.Equal("values", refused.ParamName);
    }

    [Fact]
    public void A_disposed_sender_says_it_is_the_sender_that_was_disposed()
    {
        var sender = new ZabbixSender([new ZabbixServerNode("127.0.0.1", 10051)]);
        sender.Dispose();

        ObjectDisposedException disposed = Assert.IsType<ObjectDisposedException>(Record.Exception(() => { _ = sender.SendAsync(Values(1), TestContext.Current.CancellationToken); }));

        Assert.Equal(typeof(ZabbixSender).FullName, disposed.ObjectName);
    }

    [Fact]
    public async Task A_first_frame_that_fails_is_its_own_failure_because_nothing_was_taken()
    {
        (int port, Task<IReadOnlyList<byte[]>> frames) = MockTrapper.Record(1, refuseAt: 0);
        using var sender = new ZabbixSender("127.0.0.1", port) { BatchSize = 4 };

        await Assert.ThrowsAsync<ZabbixTrapperRefusedException>(() => sender.SendAsync(Values(14), TestContext.Current.CancellationToken));

        Assert.Single(await frames.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_transport_failure_after_a_taken_frame_keeps_its_exception_inside_the_incomplete_send()
    {
        int calls = 0;
        ZabbixExchangeCallback exchange = (_, _) => ++calls == 1
            ? ValueTask.FromResult<ReadOnlyMemory<byte>>(System.Text.Encoding.UTF8.GetBytes(MockTrapper.Success(2, 0)))
            : ValueTask.FromException<ReadOnlyMemory<byte>>(new TimeoutException("The exchange passed its deadline."));
        using var sender = new ZabbixSender(exchange, "scripted") { BatchSize = 2 };

        ZabbixSenderIncompleteException stopped = await Assert.ThrowsAsync<ZabbixSenderIncompleteException>(() => sender.SendAsync(Values(5), TestContext.Current.CancellationToken));

        Assert.IsType<TimeoutException>(stopped.InnerException);
        Assert.Equal(3, stopped.Unconfirmed.Count);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_cancellation_after_a_taken_frame_says_what_was_taken_and_is_still_a_cancellation()
    {
        using var stop = new CancellationTokenSource();
        int calls = 0;
        ZabbixExchangeCallback exchange = async (_, token) =>
        {
            if (++calls == 1) return System.Text.Encoding.UTF8.GetBytes(MockTrapper.Success(2, 0));
            await stop.CancelAsync();                                                       // the caller cancels during the second frame
            token.ThrowIfCancellationRequested();
            return default;
        };
        using var sender = new ZabbixSender(exchange, "scripted") { BatchSize = 2 };
        IReadOnlyList<ZabbixValue> values = Values(5);

        ZabbixSenderCanceledException cancelled = await Assert.ThrowsAsync<ZabbixSenderCanceledException>(() => sender.SendAsync(values, stop.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(cancelled);                   // a catch (OperationCanceledException) still sees it
        Assert.Equal(stop.Token, cancelled.CancellationToken);
        Assert.Equal((1, 2), (cancelled.Delivered!.Frames, cancelled.Delivered.Total));
        Assert.Equal(values.Skip(2), cancelled.Unconfirmed);
    }

    [Fact]
    public async Task A_cancellation_before_any_frame_was_taken_is_a_plain_cancellation()
    {
        using var stop = new CancellationTokenSource();
        ZabbixExchangeCallback exchange = async (_, token) =>
        {
            await stop.CancelAsync();
            token.ThrowIfCancellationRequested();
            return default;
        };
        using var sender = new ZabbixSender(exchange, "scripted") { BatchSize = 2 };

        OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Values(5), stop.Token));

        Assert.IsNotType<ZabbixSenderCanceledException>(cancelled);
    }

    [Fact]
    public async Task A_send_loop_s_batch_goes_as_one_frame_whatever_the_sender_s_batch_size()
    {
        List<byte[]> sent = [];
        using var sender = new ZabbixSender(Recording(sent), "scripted") { BatchSize = 2 };

        ZabbixSendOutcome delivery = await sender.DeliverAsync(Values(5), TestContext.Current.CancellationToken);

        Assert.Equal(ZabbixSendOutcome.Delivered, delivery);
        Assert.Equal(5, MockTrapper.Values(Assert.Single(sent)));
    }

    private static ZabbixExchangeCallback Recording(List<byte[]> sent) => (request, _) =>
    {
        sent.Add(request.ToArray());
        return ValueTask.FromResult<ReadOnlyMemory<byte>>(System.Text.Encoding.UTF8.GetBytes(MockTrapper.Success(MockTrapper.Values(sent[^1]), 0)));
    };

    private static IReadOnlyList<ZabbixValue> Values(int count, string? value = null)
        => [.. Enumerable.Range(0, count).Select(i => new ZabbixValue("web-01", "cpu", value ?? i.ToString(CultureInfo.InvariantCulture), DateTimeOffset.UnixEpoch))];

    private static IEnumerable<string> ValuesOf(byte[] frame)
    {
        using var document = System.Text.Json.JsonDocument.Parse(frame);
        return [.. document.RootElement.GetProperty("data").EnumerateArray().Select(row => row.GetProperty("value").GetString()!)];
    }
}
