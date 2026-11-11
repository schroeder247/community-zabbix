// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>The framed-TCP substrate: the header in every form the protocol has, the node value type, the HA node list, and the getter.</summary>
public sealed class WireTests
{
    private static ReadOnlyMemory<byte> Frame(ReadOnlySpan<byte> payload, bool compress = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        ZabbixHeader.Write(buffer, payload, compress);
        return buffer.WrittenMemory;
    }

    [Fact]
    public void A_read_payload_does_not_alias_the_buffer_it_was_read_from()
    {
        byte[] wire = Frame("""{"request":"sender data"}"""u8).ToArray();
        var sequence = new ReadOnlySequence<byte>(wire);
        Assert.True(ZabbixHeader.TryRead(ref sequence, out ReadOnlyMemory<byte> read));

        Array.Clear(wire);                                                              // the pipe hands the segment back and it is reused

        Assert.Equal("""{"request":"sender data"}""", Encoding.UTF8.GetString(read.Span));
    }

    [Fact]
    public void A_compressed_frame_is_smaller_and_inflates_to_the_payload()
    {
        byte[] payload = Encoding.UTF8.GetBytes(new string('a', 10_000));
        ReadOnlyMemory<byte> frame = Frame(payload, compress: true);
        Assert.True(frame.Length < payload.Length);
        Assert.Equal(0x03, frame.Span[4]);

        var sequence = new ReadOnlySequence<byte>(frame);
        Assert.True(ZabbixHeader.TryRead(ref sequence, out ReadOnlyMemory<byte> read));

        Assert.Equal(payload, read.ToArray());
        Assert.Equal(0, sequence.Length);
    }

    [Fact]
    public void A_payload_that_does_not_shrink_is_sent_plain()
    {
        byte[] payload = [1, 2, 3];
        ReadOnlyMemory<byte> frame = Frame(payload, compress: true);

        Assert.Equal(0x01, frame.Span[4]);
        Assert.Equal(13 + 3, frame.Length);
    }

    [Fact]
    public void A_partial_frame_asks_for_more_and_a_large_header_is_read_where_large_frames_are_allowed()
    {
        byte[] payload = Encoding.UTF8.GetBytes("large");
        var large = new byte[21 + payload.Length];
        "ZBXD"u8.CopyTo(large);
        large[4] = 0x05;
        BinaryPrimitives.WriteInt64LittleEndian(large.AsSpan(5), payload.Length);
        payload.CopyTo(large, 21);

        var partial = new ReadOnlySequence<byte>(large.AsMemory(0, 15));
        Assert.False(ZabbixHeader.TryRead(ref partial, out _, allowLarge: true));
        var whole = new ReadOnlySequence<byte>(large);
        Assert.True(ZabbixHeader.TryRead(ref whole, out ReadOnlyMemory<byte> read, allowLarge: true));
        Assert.Equal("large", Encoding.UTF8.GetString(read.Span));
    }

    [Fact]
    public void A_frame_past_the_cap_is_refused_before_it_is_read()
    {
        var header = new byte[13];
        "ZBXD"u8.CopyTo(header);
        header[4] = 0x01;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), 1_000_000);
        var sequence = new ReadOnlySequence<byte>(header);

        Assert.Throws<InvalidDataException>(() => ZabbixHeader.TryRead(ref sequence, out _, maxPayload: 1024));
    }

    [Fact]
    public void A_decompression_bomb_is_refused_by_its_declared_inflated_size()
    {
        byte[] zeros = new byte[64 * 1024];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(zeros);
        byte[] body = compressed.ToArray();
        var frame = new byte[13 + body.Length];
        "ZBXD"u8.CopyTo(frame);
        frame[4] = 0x03;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(5), body.Length);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(9), zeros.Length);
        body.CopyTo(frame, 13);
        var sequence = new ReadOnlySequence<byte>(frame);

        Assert.Throws<InvalidDataException>(() => ZabbixHeader.TryRead(ref sequence, out _, maxPayload: 4096));
    }

    private static byte[] Header(byte flags, uint length, uint reserved, ReadOnlySpan<byte> body = default)
    {
        var frame = new byte[13 + body.Length];
        "ZBXD"u8.CopyTo(frame);
        frame[4] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(9), reserved);
        body.CopyTo(frame.AsSpan(13));
        return frame;
    }

    private static byte[] Deflate(ReadOnlySpan<byte> payload)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(payload);
        return compressed.ToArray();
    }

    // comms.c: `0 == (version & ZBX_TCP_PROTOCOL) || version > (ZBX_TCP_PROTOCOL | ZBX_TCP_COMPRESS | flags)`.
    [Theory]
    [InlineData(0x00)]
    [InlineData(0x02)]
    [InlineData(0x05)]
    [InlineData(0x07)]
    [InlineData(0x09)]
    [InlineData(0xFF)]
    public void A_flags_byte_upstream_refuses_is_refused(byte flags)
    {
        var sequence = new ReadOnlySequence<byte>(Header(flags, 2, 0, "{}"u8));

        Assert.Throws<InvalidDataException>(() => ZabbixHeader.TryRead(ref sequence, out _));
    }

    [Fact]
    public void A_length_with_the_high_bit_set_is_an_unsigned_length_past_the_cap()
    {
        var sequence = new ReadOnlySequence<byte>(Header(0x01, 0xFFFF_FFFF, 0));

        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => ZabbixHeader.TryRead(ref sequence, out _));

        Assert.Contains("4294967295", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_frame_whose_reserved_length_passes_the_cap_is_refused()
    {
        var sequence = new ReadOnlySequence<byte>(Header(0x01, 2, 1_000_000, "{}"u8));

        Assert.Throws<InvalidDataException>(() => ZabbixHeader.TryRead(ref sequence, out _, maxPayload: 1024));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(101)]
    public void A_compressed_frame_that_does_not_inflate_to_its_reserved_length_is_invalid_data(uint reserved)
    {
        byte[] body = Deflate(new byte[100]);
        var sequence = new ReadOnlySequence<byte>(Header(0x03, (uint)body.Length, reserved, body));

        Assert.Throws<InvalidDataException>(() => ZabbixHeader.TryRead(ref sequence, out _));
    }

    [Fact]
    public void A_frame_that_declares_a_large_inflated_size_allocates_only_what_it_delivers()
    {
        byte[] body = Deflate("tiny"u8);
        var sequence = new ReadOnlySequence<byte>(Header(0x03, (uint)body.Length, 512u * 1024 * 1024, body));
        long before = GC.GetAllocatedBytesForCurrentThread();

        Assert.Throws<InvalidDataException>(() => ZabbixHeader.TryRead(ref sequence, out _));

        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024, "a 512 MiB declaration must not allocate 512 MiB");
    }

    [Fact]
    public void The_default_caps_are_upstreams_one_gibibyte_and_sixteen_for_a_large_receive()
    {
        Assert.Equal(1L << 30, ZabbixHeader.MaxPayload);
        Assert.Equal(16L << 30, ZabbixHeader.MaxLargePayload);
        Assert.Null(new ZabbixConnectionOptions().MaxPayloadBytes);                           // unset: upstream's cap for the receive
    }

    [Fact]
    public void A_frame_declaring_more_than_a_gibibyte_is_refused_by_a_plain_receive_and_awaited_by_a_large_one()
    {
        var header = new byte[13];
        "ZBXD"u8.CopyTo(header);
        header[4] = 0x01;                                                                 // a 32-bit frame: the large cap is the receive's, not the frame's
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(5), (1u << 30) + 1);

        var plain = new ReadOnlySequence<byte>(header);
        Assert.Throws<InvalidDataException>(() => ZabbixHeader.TryRead(ref plain, out _, allowLarge: false));
        var large = new ReadOnlySequence<byte>(header);
        Assert.False(ZabbixHeader.TryRead(ref large, out _, allowLarge: true));          // within 16 GiB: the reader waits for the body
        var capped = new ReadOnlySequence<byte>(header);
        Assert.Throws<InvalidDataException>(() => ZabbixHeader.TryRead(ref capped, out _, maxPayload: 1024, allowLarge: true));   // an explicit cap wins
    }

    [Theory]
    [InlineData("zbx-server", "zbx-server", 10051)]
    [InlineData("zbx-server:10052", "zbx-server", 10052)]
    [InlineData("[::1]:10051", "::1", 10051)]
    [InlineData("[fe80::1]", "fe80::1", 10051)]
    [InlineData("fe80::1", "fe80::1", 10051)]
    [InlineData(" 10.0.0.1:10061 ", "10.0.0.1", 10061)]
    [InlineData("zbx_server:10051", "zbx_server", 10051)]
    public void A_node_parses_from_every_form_zabbix_writes(string text, string host, int port)
    {
        ZabbixServerNode node = ZabbixServerNode.Parse(text, CultureInfo.InvariantCulture);

        Assert.Equal(host, node.Host);
        Assert.Equal(port, node.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("host:")]
    [InlineData("host:99999")]
    [InlineData("[::1")]
    [InlineData(":10051")]
    [InlineData("[]")]
    [InlineData("[]:10051")]
    [InlineData("a b")]
    [InlineData("zbx1;zbx2")]
    [InlineData("zbx1,zbx2")]
    [InlineData("::1:10051")]
    [InlineData("[10.0.0.1]:10051")]
    [InlineData("[::1]:0")]
    [InlineData("[::1]10051")]
    [InlineData("host:+1")]
    public void Text_that_is_not_a_node_is_refused(string text) => Assert.False(ZabbixServerNode.TryParse(text, null, out _));

    [Theory]
    [InlineData("a b")]
    [InlineData("zbx1;zbx2")]
    [InlineData("-x")]
    public void A_host_that_is_neither_an_address_nor_a_name_is_refused_where_it_is_written(string host)
        => Assert.Throws<ArgumentException>(() => new ZabbixServerNode(host));

    [Fact]
    public void A_host_written_with_its_port_is_refused_with_how_to_pass_them()
    {
        // The message says how to pass the host and the port, not only that the text is neither an IP address nor a host name.
        ArgumentException refused = Assert.Throws<ArgumentException>(() => new ZabbixServerNode("zabbix.example:10052"));

        Assert.Contains("the host 'zabbix.example' and the port 10052 separately", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_default_node_formats_without_throwing()
    {
        ZabbixServerNode node = default;

        Assert.Equal(":0", node.ToString());
    }

    [Theory]
    [InlineData("zbx:10052")]
    [InlineData("[::1]:10051")]
    [InlineData("10.0.0.1:10051")]
    public void A_node_formats_to_utf8_as_it_formats_to_text(string text)
    {
        ZabbixServerNode node = ZabbixServerNode.Parse(text, CultureInfo.InvariantCulture);
        Span<byte> utf8 = stackalloc byte[64];
        Span<char> chars = stackalloc char[64];

        Assert.True(node.TryFormat(utf8, out int bytes, default, CultureInfo.InvariantCulture));
        Assert.True(node.TryFormat(chars, out int written, default, CultureInfo.InvariantCulture));

        Assert.Equal(text, Encoding.UTF8.GetString(utf8[..bytes]));
        Assert.Equal(text, new string(chars[..written]));
    }

    [Fact]
    public void A_node_that_does_not_fit_the_destination_reports_it_instead_of_throwing()
    {
        ZabbixServerNode node = ZabbixServerNode.Parse("zbx-server:10051", CultureInfo.InvariantCulture);

        Assert.False(node.TryFormat(stackalloc byte[4], out _, default, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_node_is_a_dns_endpoint_at_the_socket()
    {
        DnsEndPoint endPoint = ZabbixServerNode.Parse("[::1]:10052", CultureInfo.InvariantCulture).ToDnsEndPoint();

        Assert.Equal(("::1", 10052), (endPoint.Host, endPoint.Port));
    }

    [Fact]
    public void A_node_formats_back_with_ipv6_in_brackets()
    {
        Assert.Equal("zbx:10052", ZabbixServerNode.Parse("zbx:10052", CultureInfo.InvariantCulture).ToString());
        Assert.Equal("[::1]:10051", ZabbixServerNode.Parse("[::1]", CultureInfo.InvariantCulture).ToString());
    }

    [Fact]
    public async Task The_getter_asks_in_json_and_falls_back_to_plaintext_for_an_agent_before_7_0_once()
    {
        // zabbix_get (7.0+) asks in JSON with the check's timeout and falls back to plaintext when
        // the agent answers in plaintext; ZBX_ERROR is the agent's error, never a value (agent_get.c 8.0:126-130).
        using var agent = new TcpListener(IPAddress.Loopback, 0);
        agent.Start();
        int port = ((IPEndPoint)agent.LocalEndpoint).Port;
        var requests = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using TcpClient client = await agent.AcceptTcpClientAsync();
                await using NetworkStream stream = client.GetStream();
                string key = Encoding.UTF8.GetString((await ZabbixHeader.ReadAsync(stream)).Span);
                requests.Enqueue(key);
                byte[] reply = key switch                                                       // an agent before 7.0 reads JSON as a key
                {
                    "agent.ping" => "1"u8.ToArray(),
                    "broken" => "ZBX_ERROR"u8.ToArray(),
                    _ when key.StartsWith('{') => "ZBX_NOTSUPPORTED\0Invalid item key format."u8.ToArray(),
                    _ => "ZBX_NOTSUPPORTED\0Unsupported item key."u8.ToArray(),
                };
                await ZabbixHeader.WriteAsync(stream, reply);
            }
        }, TestContext.Current.CancellationToken);
        var logs = new FakeLogCollector();
        using var getter = new ZabbixGetter(new ZabbixServerNode("127.0.0.1", port), null, null, new FakeLogger<ZabbixGetter>(logs));

        ZabbixAgentAnswer ping = await getter.GetAsync("agent.ping", TestContext.Current.CancellationToken);
        ZabbixAgentAnswer unknown = await getter.GetAsync("no.such.key", TestContext.Current.CancellationToken);
        ZabbixAgentAnswer broken = await getter.GetAsync("broken", TestContext.Current.CancellationToken);

        Assert.Equal("1", ping.Value);
        Assert.False(unknown.IsSupported);
        Assert.Equal("Unsupported item key.", unknown.Error);
        Assert.False(broken.IsSupported);
        Assert.Equal("Zabbix Agent non-critical error", broken.Error);
        Assert.Equal("""{"request":"passive checks","data":[{"key":"agent.ping","timeout":3}]}""", requests.First());
        Assert.Equal(["agent.ping", "no.such.key", "broken"], requests.Skip(1));             // plaintext from then on, remembered
        Assert.Equal(3, logs.GetSnapshot().Count(record => record.Id.Id == 1200));
    }

    [Theory]
    [InlineData("""{"version":"7.0.0","variant":1,"data":[{"value":"1"}]}""", "1", null)]
    [InlineData("""{"version":"7.0.0","variant":1,"data":[{"value":null}]}""", null, null)]
    [InlineData("""{"version":"7.0.0","variant":1,"data":[{"error":"Unsupported item key."}]}""", null, "Unsupported item key.")]
    [InlineData("""{"version":"7.0.0","variant":1,"data":[{"error":""}]}""", null, "Not supported by Zabbix Agent")]
    public async Task The_getter_reads_a_7_0_agent_s_json_answer_as_upstream_does(string reply, string? value, string? error)
    {
        using var getter = new ZabbixGetter((_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(reply)), "scripted");

        ZabbixAgentAnswer answer = await getter.GetAsync("agent.ping", TestContext.Current.CancellationToken);

        Assert.Equal(value, answer.Value);
        Assert.Equal(error, answer.Error);
    }

    [Fact]
    public async Task A_json_request_the_agent_refused_whole_is_a_refusal_not_an_exchange_failure()
    {
        var logs = new FakeLogCollector();
        using var getter = new ZabbixGetter((_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>("""{"version":"7.0.0","variant":1,"error":"unknown request"}"""u8.ToArray()), "scripted",
            new FakeLogger<ZabbixGetter>(logs));

        ZabbixAgentRefusedException refused = await Assert.ThrowsAsync<ZabbixAgentRefusedException>(() => getter.GetAsync("agent.ping", TestContext.Current.CancellationToken));

        Assert.Equal("unknown request", refused.Error);
        Assert.Equal("agent.ping", refused.Key.ToString());
        Assert.False(ZabbixFrames.IsExchangeFailure(refused));
        Assert.Equal("refused", Zabbix.Diagnostics.ZabbixErrorType.Of(refused));
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1202 && record.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Id.Id == 1201);
    }

    [Fact]
    public async Task A_getter_that_cannot_reach_its_agent_logs_it_and_throws()
    {
        using var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        int port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var logs = new FakeLogCollector();
        using var getter = new ZabbixGetter(new ZabbixServerNode("127.0.0.1", port), null, null, new FakeLogger<ZabbixGetter>(logs));

        await Assert.ThrowsAsync<SocketException>(async () => await getter.GetAsync("agent.ping", TestContext.Current.CancellationToken));

        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1201 && record.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", 16)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", 24)]
    public void A_psk_parses_from_hex(string hex, int bytes) => Assert.Equal(bytes, ZabbixTls.ParsePsk(hex).Length);

    [Theory]
    [InlineData("0123")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    public void A_psk_that_is_too_short_or_not_hex_is_refused(string hex) => Assert.ThrowsAny<FormatException>(() => ZabbixTls.ParsePsk(hex));

    [Fact]
    public void Each_facade_built_from_a_host_takes_its_own_default_port()
    {
        using var getter = new ZabbixGetter("agent.example");
        using var sender = new ZabbixSender("zbx.example");

        Assert.Equal("agent.example:10050", getter.Destination);                      // the agent's passive port, not the server's
        Assert.Equal("zbx.example:10051", sender.Destination);
        Assert.Equal(new ZabbixServerNode("zbx.example", 10051), new ZabbixServerNode("zbx.example"));
        Assert.Equal(new ZabbixComponentVersion(7, 4, 0), new ZabbixComponentVersion(7, 4));
    }
}
