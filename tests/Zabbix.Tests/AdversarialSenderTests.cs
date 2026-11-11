// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Community.Zabbix.Hosting;
using Community.Zabbix.Listening;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The trapper protocol from both ends — the sender and the server wire reading replies a server
/// should never send (counters that overflow, the wrong JSON kinds, a reply past the cap, a port that resets), and the
/// SDK's own trapper reading frames a sender should never send. A sender's reply always ends in a verdict for the send
/// loop (<see cref="ZabbixDelivery"/>) and a documented exception for a direct caller; a trapper always answers
/// <c>success</c> or <c>failed</c>.
/// </summary>
[Collection("Adversarial wire stress")]
public sealed class AdversarialSenderTests
{
    private static readonly ZabbixValue[] OneValue = [new("h", "k", "1", DateTimeOffset.UnixEpoch)];

    private static ZabbixSender Answering(string reply) => new((_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(reply)), "scripted");

    // ---- the sender --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"response":"failed"}""")]
    [InlineData("""{"response":"failed","info":"cannot process request"}""")]
    [InlineData("""{"response":"success","info":"processed: 1; failed: 0; total: 1"}""")]
    [InlineData("""{"response":"SUCCESS","info":"processed: 1; failed: 0; total: 1; seconds spent: 0.000100"}""")]
    [InlineData("""{"response":null,"info":"processed: 1; failed: 0; total: 1; seconds spent: 0.000100"}""")]
    [InlineData("""{"info":"processed: 1; failed: 0; total: 1; seconds spent: 0.000100"}""")]
    [InlineData("""{"response":"success","info":"processed: -1; failed: 0; total: 1; seconds spent: 0.000100"}""")]
    public async Task A_reply_that_is_not_a_counted_success_is_a_sender_exception_and_a_rejected_batch(string reply)
    {
        using ZabbixSender sender = Answering(reply);

        await Assert.ThrowsAsync<ZabbixTrapperRefusedException>(() => sender.SendAsync(OneValue, TestContext.Current.CancellationToken));
        Assert.Equal(ZabbixDelivery.Rejected, await sender.DeliverAsync(OneValue, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("not json")]
    [InlineData("""{"response":1}""")]
    [InlineData("""{"response":"success","info":5}""")]
    [InlineData("""{"response":"success","info":["processed: 1"]}""")]
    [InlineData("""{"response":"success","info":"processed: 1; failed""")]
    public async Task A_reply_that_is_not_the_trapper_s_json_is_invalid_data_and_a_retry(string reply)
    {
        using ZabbixSender sender = Answering(reply);

        await Assert.ThrowsAsync<InvalidDataException>(() => sender.SendAsync(OneValue, TestContext.Current.CancellationToken));
        Assert.Equal(ZabbixDelivery.Retried, await sender.DeliverAsync(OneValue, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_counted_success_with_a_trailing_newline_is_read_as_its_counters()
    {
        using ZabbixSender sender = Answering("{\"response\":\"success\",\"info\":\"processed: 2; failed: 1; total: 3; seconds spent: 0.000055\\n\"}");

        ZabbixSenderResponse response = await sender.SendAsync(OneValue, TestContext.Current.CancellationToken);

        Assert.Equal((2, 1, 3), (response.Processed, response.Failed, response.Total));
    }

    // ZabbixSender.Parse reads the info line's counters without trusting their range or form: a counter past int
    // ("processed: 99999999999") or a malformed decimal ("seconds spent: 1.2.3", ".") is a ZabbixTrapperRefusedException, as an
    // uncountable info already is. An OverflowException or FormatException is not in SendAsync's documented set, and the
    // send loop (which retries any throw) would send the batch again and again although the server said success:
    // duplicates.
    [Theory]
    [InlineData("processed: 99999999999; failed: 0; total: 99999999999; seconds spent: 0.000100")]
    [InlineData("processed: 1; failed: 0; total: 1; seconds spent: 1.2.3")]
    [InlineData("processed: 1; failed: 0; total: 1; seconds spent: .")]
    public async Task An_info_line_whose_counters_do_not_parse_is_a_sender_exception_and_a_rejected_batch(string info)
    {
        using ZabbixSender sender = Answering("{\"response\":\"success\",\"info\":\"" + info + "\"}");

        await Assert.ThrowsAsync<ZabbixTrapperRefusedException>(() => sender.SendAsync(OneValue, TestContext.Current.CancellationToken));
        Assert.Equal(ZabbixDelivery.Rejected, await sender.DeliverAsync(OneValue, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_reply_frame_past_the_sender_s_cap_is_refused_from_its_header_and_is_a_retry()
    {
        string big = "{\"response\":\"success\",\"info\":\"" + new string('x', 4096) + "\"}";
        await using var peer = new FramesTests.Peer((stream, _, stopping) => ZabbixHeader.WriteAsync(stream, Encoding.UTF8.GetBytes(big), stopping));
        var logs = new FakeLogCollector();
        using var sender = new ZabbixSender([peer.Node], new ZabbixConnectionOptions { MaxPayloadBytes = 1024 }, null, new FakeLogger<ZabbixSender>(logs));

        await Assert.ThrowsAsync<InvalidDataException>(() => sender.SendAsync(OneValue, TestContext.Current.CancellationToken));
        Assert.Equal(ZabbixDelivery.Retried, await sender.DeliverAsync(OneValue, TestContext.Current.CancellationToken));
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1104);                          // the frame was refused, and said why
    }

    [Fact]
    public async Task A_port_that_accepts_and_resets_before_reading_is_a_retry_and_is_logged()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task resetting = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
                client.Client.LingerState = new LingerOption(true, 0);                                  // RST on close
            }
        }, TestContext.Current.CancellationToken);
        var logs = new FakeLogCollector();
        using var sender = new ZabbixSender([new ZabbixServerNode("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port)], null, null, new FakeLogger<ZabbixSender>(logs));

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync(OneValue, TestContext.Current.CancellationToken));
        ZabbixDelivery verdict = await sender.DeliverAsync(OneValue, TestContext.Current.CancellationToken);
        await resetting;

        Assert.True(ZabbixFrames.IsExchangeFailure(failure), failure.GetType().FullName);
        Assert.Equal(ZabbixDelivery.Retried, verdict);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1003);
    }

    // ---- the server wire ----------------------------------------------------------------------------------------------

    private static ZabbixServerWire Wire(string reply) => new((_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(reply)), "sid");

    [Theory]
    [InlineData("""{"response":"failed"}""")]
    [InlineData("""{"response":"maybe"}""")]
    [InlineData("""[]""")]
    [InlineData("""not json""")]
    [InlineData("""{"response":"success"}""")]
    [InlineData("""{"response":"success","data":null}""")]
    public async Task A_queue_reply_that_is_not_the_server_s_shape_is_invalid_data(string reply)
    {
        using ZabbixServerWire wire = Wire(reply);

        await Assert.ThrowsAsync<InvalidDataException>(() => wire.GetQueueAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    // A reply that parsed but is not the server's shape is InvalidDataException naming the request, for the typed reads
    // after JsonDocument.Parse too: root.Deserialize(ZabbixServerReply), the per-request Deserialize of `data`, and
    // GetQueueSizeAsync's queue read. A JsonException (a member of the wrong kind) or an InvalidOperationException
    // (`queue` as a string) let out as they are would escape the caller's documented failure set.
    [Theory]
    [InlineData("""{"response":5}""", "queue")]
    [InlineData("""{"response":"success","info":5,"data":[]}""", "queue")]
    [InlineData("""{"response":"failed","info":5}""", "queue")]
    [InlineData("""{"response":"success","data":"not a list"}""", "queue")]
    [InlineData("""{"response":"success","data":[{"itemid":"abc","nextcheck":1}]}""", "details")]
    [InlineData("""{"response":"success","data":[],"total":"many"}""", "details")]
    [InlineData("""{"response":"success","queue":"5"}""", "size")]
    public async Task A_reply_whose_members_are_the_wrong_kind_is_invalid_data_naming_the_request(string reply, string call)
    {
        using ZabbixServerWire wire = Wire(reply);

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => call switch
        {
            "queue" => wire.GetQueueAsync(cancellationToken: TestContext.Current.CancellationToken),
            "details" => wire.GetQueueDetailsAsync(10, TestContext.Current.CancellationToken),
            _ => wire.GetQueueSizeAsync(cancellationToken: TestContext.Current.CancellationToken),
        });

        Assert.Contains(call == "size" ? "zabbix.stats" : "queue.get", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_carries_the_server_s_info_even_when_it_holds_escapes_and_control_characters()
    {
        using ZabbixServerWire wire = Wire("""{"response":"failed","info":"Permission denied.\u0000\n😀"}""");

        ZabbixServerRefusedException refused = await Assert.ThrowsAsync<ZabbixServerRefusedException>(() => wire.PingAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Permission denied.\u0000\n\U0001F600", refused.Info);
    }

    // ---- the SDK's trapper ------------------------------------------------------------------------------------------------

    private static async Task<(string Response, string Info)> TrapAsync(ZabbixTrapper trapper, string frame)
    {
        ReadOnlyMemory<byte> reply = await trapper.Handler(new ZabbixRequest(Encoding.UTF8.GetBytes(frame), new IPEndPoint(IPAddress.Loopback, 1)), TestContext.Current.CancellationToken);
        using JsonDocument answer = JsonDocument.Parse(reply);
        return (answer.RootElement.GetProperty("response").GetString()!, answer.RootElement.GetProperty("info").GetString()!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"request":5}""")]
    [InlineData("""{"request":"sender data"}""")]
    [InlineData("""{"request":"sender data","data":null}""")]
    [InlineData("""{"request":"sender data","data":{}}""")]
    [InlineData("""{"request":"sender data","data":[{"host":"h","key":"k","value":"v","clock":"soon"}]}""")]
    [InlineData("""{"request":"sender data","data":[{"host":"h","key":"k","value":"v","ns":99999999999}]}""")]
    [InlineData("""{"request":"active checks","host":"h"}""")]
    public async Task A_frame_that_is_not_sender_data_is_answered_failed(string frame)
    {
        var trapper = new ZabbixTrapper(Channel.CreateUnbounded<ZabbixValue>().Writer);

        (string response, _) = await TrapAsync(trapper, frame);

        Assert.Equal("failed", response);
    }

    [Theory]
    [InlineData("""{"host":"","key":"k","value":"v"}""")]
    [InlineData("""{"host":"h","key":"","value":"v"}""")]
    [InlineData("""{"key":"k","value":"v"}""")]
    [InlineData("""{"host":"h","value":"v"}""")]
    [InlineData("""{"host":"h","key":"bad[key","value":"v"}""")]
    [InlineData("""{"host":"h","key":"k","value":"v","ns":-1}""")]
    [InlineData("""{"host":"h","key":"k","value":"v","ns":1000000000}""")]
    public async Task A_value_the_trapper_cannot_take_is_counted_failed_and_the_rest_of_the_frame_is_taken(string item)
    {
        Channel<ZabbixValue> edge = Channel.CreateUnbounded<ZabbixValue>();
        var trapper = new ZabbixTrapper(edge.Writer);

        (string response, string info) = await TrapAsync(trapper, "{\"request\":\"sender data\",\"data\":[" + item + ",{\"host\":\"h\",\"key\":\"ok\",\"value\":\"1\"}]}");

        Assert.Equal("success", response);
        Assert.StartsWith("processed: 1; failed: 1; total: 2; seconds spent: ", info, StringComparison.Ordinal);
        Assert.True(edge.Reader.TryRead(out ZabbixValue? taken));
        Assert.Equal("ok", taken.Key.ToString());
    }

    [Fact]
    public async Task A_clock_at_the_edge_of_long_is_taken_as_sent()
    {
        Channel<ZabbixValue> edge = Channel.CreateUnbounded<ZabbixValue>();
        var trapper = new ZabbixTrapper(edge.Writer);

        (string response, string info) = await TrapAsync(trapper, """{"request":"sender data","data":[{"host":"h","key":"k","value":"v","clock":-1,"ns":0}]}""");

        Assert.Equal(("success", true), (response, info.StartsWith("processed: 1;", StringComparison.Ordinal)));
    }

    // A frame with a null element in `data` (`"data":[null]`) is not a handler fault: upstream skips an element it cannot
    // open as an object (proxy.c, zbx_json_brackets_open fails, so the row is not taken) and counts the rest. A
    // NullReferenceException out of the handler would make the listener's fault contract answer `failed` for the whole
    // frame and log 6005 at Error, once per such frame from any allowed peer.
    [Fact]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The defect is an exception type the handler must not throw; the test records it.")]
    public async Task A_null_element_in_sender_data_is_counted_failed_not_a_handler_fault()
    {
        var trapper = new ZabbixTrapper(Channel.CreateUnbounded<ZabbixValue>().Writer);

        (string response, string info) = await TrapAsync(trapper, """{"request":"sender data","data":[null,{"host":"h","key":"k","value":"1"}]}""");

        Assert.Equal("success", response);
        Assert.StartsWith("processed: 1; failed: 1; total: 2;", info, StringComparison.Ordinal);
    }

    // Upstream reads `value` with zbx_json_value_by_name_dyn (proxy.c, parse_history_data_row_value), which takes any
    // JSON scalar as its text, so {"value":1} is the value "1". A serializer that refuses a number would make the
    // trapper answer the whole frame "not a sender data request": every value in it lost for one numeric field (a
    // script's json.dumps of an int).
    [Fact]
    public async Task A_numeric_value_is_taken_as_its_text_as_upstream_takes_it()
    {
        Channel<ZabbixValue> edge = Channel.CreateUnbounded<ZabbixValue>();
        var trapper = new ZabbixTrapper(edge.Writer);

        (string response, _) = await TrapAsync(trapper, """{"request":"sender data","data":[{"host":"h","key":"k","value":42.5}]}""");

        Assert.Equal("success", response);
        Assert.True(edge.Reader.TryRead(out ZabbixValue? taken));
        Assert.Equal("42.5", taken.Value);
    }
}
