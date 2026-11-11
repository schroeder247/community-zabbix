// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;
using Community.Zabbix.Protocol;

namespace Community.Zabbix.Tests;

/// <summary>
/// The Zabbix header under fuzz. The invariant for every input is the reader's contract — a payload,
/// "need more bytes" (<see langword="false"/>), or <see cref="InvalidDataException"/>; never another exception, never
/// a hang, never an allocation out of proportion to the bytes delivered. Upstream's reference is <c>comms.c</c>
/// <c>zbx_tcp_recv_context</c> and <c>compress.c</c> <c>zbx_uncompress</c> (zlib's <c>uncompress</c>), 7.0/7.4/8.0 alike.
/// </summary>
public sealed class AdversarialFrameTests
{
    private enum Outcome { Payload, NeedMore, Refused }

    private static byte[] Deflate(ReadOnlySpan<byte> payload)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(payload);
        return compressed.ToArray();
    }

    private static byte[] Small(byte flags, uint length, uint reserved, ReadOnlySpan<byte> body = default)
    {
        var frame = new byte[13 + body.Length];
        "ZBXD"u8.CopyTo(frame);
        frame[4] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(9), reserved);
        body.CopyTo(frame.AsSpan(13));
        return frame;
    }

    private static byte[] Large(byte flags, ulong length, ulong reserved, ReadOnlySpan<byte> body = default)
    {
        var frame = new byte[21 + body.Length];
        "ZBXD"u8.CopyTo(frame);
        frame[4] = flags;
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(5), length);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(13), reserved);
        body.CopyTo(frame.AsSpan(21));
        return frame;
    }

    private static Outcome Read(byte[] bytes, long? cap, bool allowLarge, out ReadOnlyMemory<byte> payload, out long rest)
    {
        var buffer = new ReadOnlySequence<byte>(bytes);
        try
        {
            Outcome outcome = ZabbixHeader.TryRead(ref buffer, out payload, cap, allowLarge) ? Outcome.Payload : Outcome.NeedMore;
            rest = buffer.Length;
            return outcome;
        }
        catch (InvalidDataException)
        {
            payload = default;
            rest = -1;
            return Outcome.Refused;
        }
    }

    private static Outcome Read(byte[] bytes, long? cap = null, bool allowLarge = false) => Read(bytes, cap, allowLarge, out _, out _);

    // ---- the seeded fuzz ----------------------------------------------------------------------------------------------

    private static readonly byte[] InterestingFlags = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0F, 0x10, 0x81, 0xFF];

    private static readonly long?[] InterestingCaps = [null, 0, 1, 16, 1024, 1 << 20, ZabbixHeader.MaxPayload, -5];

    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "A seeded fuzz must be reproducible; nothing here is a secret.")]
    private static ulong PickLength(Random random, long? cap, int bodyLength, bool large)
    {
        long c = cap is { } set ? Math.Max(set, 0) : (large ? ZabbixHeader.MaxLargePayload : ZabbixHeader.MaxPayload);
        ulong[] choices =
        [
            0, 1, (ulong)bodyLength, (ulong)Math.Max(bodyLength - 1, 0), (ulong)bodyLength + 1,
            (ulong)Math.Max(c - 1, 0), (ulong)c, (ulong)c + 1, uint.MaxValue, int.MaxValue, (ulong)Array.MaxLength, (ulong)Array.MaxLength + 1,
            (ulong)random.Next(0, 4096),
            .. large ? new ulong[] { 1UL << 32, 1UL << 40, (ulong)ZabbixHeader.MaxLargePayload, (ulong)ZabbixHeader.MaxLargePayload + 1, long.MaxValue, ulong.MaxValue } : [],
        ];
        return choices[random.Next(choices.Length)];
    }

    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "A seeded fuzz must be reproducible; nothing here is a secret.")]
    private static byte[] Mutant(Random random, out long? cap, out bool allowLarge)
    {
        cap = InterestingCaps[random.Next(InterestingCaps.Length)];
        allowLarge = random.Next(2) == 0;
        byte flags = random.Next(4) == 0 ? (byte)random.Next(256) : InterestingFlags[random.Next(InterestingFlags.Length)];
        bool large = (flags & 0x04) != 0;

        byte[] body;
        switch (random.Next(5))
        {
            case 0:
                body = new byte[random.Next(0, 64)];
                random.NextBytes(body);
                break;
            case 1:
                {
                    var plain = new byte[random.Next(0, 4096)];
                    if (random.Next(2) == 0) random.NextBytes(plain);
                    body = Deflate(plain);
                    break;
                }
            case 2:
                {
                    byte[] valid = Deflate(Encoding.UTF8.GetBytes(new string('z', random.Next(0, 2048))));
                    body = valid;
                    if (valid.Length > 0) body[random.Next(valid.Length)] ^= (byte)(1 << random.Next(8));      // one bit flipped
                    break;
                }
            case 3:
                {
                    byte[] valid = Deflate(Encoding.UTF8.GetBytes("{\"request\":\"sender data\"}"));
                    body = [.. valid, .. Encoding.ASCII.GetBytes("trailing")];
                    break;
                }
            default:
                body = [];
                break;
        }

        ulong length = PickLength(random, cap, body.Length, large);
        ulong reserved = PickLength(random, cap, body.Length * 4, large);
        byte[] frame = large ? Large(flags, length, reserved, body) : Small(flags, (uint)length, (uint)reserved, body);

        if (random.Next(10) == 0) frame[random.Next(4)] ^= 0x20;                                     // a bad magic
        if (random.Next(4) == 0) frame = frame[..random.Next(frame.Length + 1)];                    // truncated anywhere
        if (random.Next(6) == 0)
        {
            var garbage = new byte[random.Next(1, 32)];
            random.NextBytes(garbage);
            frame = [.. frame, .. garbage];                                                           // trailing garbage
        }
        return frame;
    }

    // The fuzz includes compressed frames (0x03 or 0x07) that declare a zero-length body or a zero `reserved`
    // (15, 7, 11 and 10 of 2 500 frames on the four seeds, e.g. 5A425844 03 00000000 00000000: 13 bytes, nothing after
    // the header); such a frame must be refused as invalid data, not escape as ArgumentOutOfRangeException.
    [Theory]
    [InlineData(20260927)]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(65535)]
    public void Every_seeded_random_frame_yields_a_payload_more_bytes_or_invalid_data_within_a_bounded_allocation(int seed)
        => Fuzz(seed);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The invariant under test is that nothing but InvalidDataException escapes; any other type is recorded as the failure.")]
    private static void Fuzz(int seed)
    {
        const int Cases = 2500;                                                                     // four seeds: 10 000 frames
        var random = new Random(seed);
        var broken = new List<string>();

        for (int i = 0; i < Cases; i++)
        {
            byte[] frame = Mutant(random, out long? cap, out bool allowLarge);
            long before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                Read(frame, cap, allowLarge, out ReadOnlyMemory<byte> payload, out long rest);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                long bound = (256 * 1024) + (8L * frame.Length) + (4L * payload.Length);
                if (allocated > bound)
                    broken.Add(string.Create(CultureInfo.InvariantCulture, $"seed {seed} case {i}: allocated {allocated} bytes for a {frame.Length}-byte frame ({Convert.ToHexString(frame.AsSpan(0, Math.Min(frame.Length, 21)))})"));
                if (rest > frame.Length) broken.Add(string.Create(CultureInfo.InvariantCulture, $"seed {seed} case {i}: the buffer grew"));
            }
            catch (Exception other)
            {
                broken.Add(string.Create(CultureInfo.InvariantCulture, $"seed {seed} case {i}: {other.GetType().Name}: {other.Message} ({Convert.ToHexString(frame.AsSpan(0, Math.Min(frame.Length, 21)))})"));
            }
        }

        Assert.True(broken.Count == 0, $"{broken.Count} of {Cases} frames broke the contract:\n" + string.Join('\n', broken.Take(8)));
    }

    // ---- truncation, pipelining, trailing garbage ------------------------------------------------------------------------

    public static TheoryData<bool, bool> Shapes => new() { { false, false }, { true, false }, { false, true }, { true, true } };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Every_prefix_of_a_valid_frame_asks_for_more_bytes_and_only_the_whole_frame_yields_the_payload(bool compressed, bool large)
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"request\":\"sender data\",\"data\":[" + string.Join(',', Enumerable.Repeat("{\"host\":\"h\",\"key\":\"k\",\"value\":\"1\"}", 40)) + "]}");
        byte[] body = compressed ? Deflate(payload) : payload;
        byte flags = (byte)(0x01 | (compressed ? 0x02 : 0) | (large ? 0x04 : 0));
        byte[] frame = large ? Large(flags, (ulong)body.Length, compressed ? (ulong)payload.Length : 0, body) : Small(flags, (uint)body.Length, compressed ? (uint)payload.Length : 0, body);

        var wrong = new List<int>();
        for (int cut = 0; cut < frame.Length; cut++)
            if (Read(frame[..cut], allowLarge: large) != Outcome.NeedMore) wrong.Add(cut);
        Outcome whole = Read(frame, null, large, out ReadOnlyMemory<byte> read, out long rest);

        Assert.Empty(wrong);
        Assert.Equal(Outcome.Payload, whole);
        Assert.Equal(payload, read.ToArray());
        Assert.Equal(0, rest);
    }

    [Fact]
    public void Trailing_bytes_after_a_frame_stay_in_the_buffer_and_are_refused_as_the_next_frame()
    {
        byte[] frame = [.. Small(0x01, 5, 0, "hello"u8), .. "garbage!"u8];

        Outcome first = Read(frame, null, false, out ReadOnlyMemory<byte> payload, out long rest);
        Outcome second = Read(frame[^(int)rest..]);

        Assert.Equal((Outcome.Payload, "hello", 8L), (first, Encoding.UTF8.GetString(payload.Span), rest));
        Assert.Equal(Outcome.Refused, second);
    }

    [Fact]
    public async Task Two_frames_pipelined_in_one_read_both_arrive_through_one_pipe_reader()
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync((byte[])[.. Small(0x01, 3, 0, "one"u8), .. Small(0x01, 3, 0, "two"u8)], TestContext.Current.CancellationToken);
        await pipe.Writer.CompleteAsync();

        ReadOnlyMemory<byte> first = await ZabbixHeader.ReadAsync(pipe.Reader, cancellationToken: TestContext.Current.CancellationToken);
        ReadOnlyMemory<byte> second = await ZabbixHeader.ReadAsync(pipe.Reader, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(("one", "two"), (Encoding.UTF8.GetString(first.Span), Encoding.UTF8.GetString(second.Span)));
    }

    // Two frames that arrive in one read both come through the stream overload. A PipeReader wrapped per call and
    // completed on return would drop whatever the pipe read past the first frame (its minimum read is 4 KiB), and the
    // second read would throw InvalidDataException("the connection ended before a whole frame arrived").
    [Fact]
    public async Task Two_frames_pipelined_in_one_read_both_arrive_through_the_stream_overload()
    {
        using var stream = new MemoryStream([.. Small(0x01, 3, 0, "one"u8), .. Small(0x01, 3, 0, "two"u8)]);

        ReadOnlyMemory<byte> first = await ZabbixHeader.ReadAsync(stream, TestContext.Current.CancellationToken);
        ReadOnlyMemory<byte> second = await ZabbixHeader.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(("one", "two"), (Encoding.UTF8.GetString(first.Span), Encoding.UTF8.GetString(second.Span)));
    }

    [Fact]
    public async Task A_stream_that_ends_inside_a_frame_is_invalid_data_not_a_hang()
    {
        using var stream = new MemoryStream(Small(0x01, 100, 0, "short"u8));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await ZabbixHeader.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    // ---- declared and reserved lengths at the edges ---------------------------------------------------------------------

    [Theory]
    [InlineData(0u, Outcome.Payload)]
    [InlineData(1u, Outcome.Payload)]
    [InlineData(1023u, Outcome.Payload)]
    [InlineData(1024u, Outcome.Payload)]
    [InlineData(1025u, Outcome.Refused)]
    [InlineData(uint.MaxValue, Outcome.Refused)]
    public void A_declared_length_at_the_cap_is_read_and_one_past_it_is_refused(uint length, object expected)
    {
        byte[] body = new byte[Math.Min(length, 2048u)];
        byte[] frame = Small(0x01, length, 0, body);

        Assert.Equal((Outcome)expected, Read(frame, cap: 1024));
    }

    [Theory]
    [InlineData(1024u, Outcome.Payload)]
    [InlineData(1025u, Outcome.Refused)]
    [InlineData(uint.MaxValue, Outcome.Refused)]
    public void A_plain_frame_s_reserved_length_is_held_to_the_same_cap(uint reserved, object expected)
        => Assert.Equal((Outcome)expected, Read(Small(0x01, 2, reserved, "{}"u8), cap: 1024));

    [Theory]
    [InlineData(1UL << 32)]
    [InlineData(1UL << 40)]
    [InlineData((ulong)long.MaxValue)]
    [InlineData(ulong.MaxValue)]
    public void A_large_frame_declaring_more_than_one_array_holds_is_refused_before_it_is_read(ulong length)
        => Assert.Equal(Outcome.Refused, Read(Large(0x05, length, 0, "tiny"u8), allowLarge: true));

    [Theory]
    [InlineData(0x05)]
    [InlineData(0x07)]
    public void A_large_frame_where_large_frames_are_not_allowed_is_refused_by_its_flags(byte flags)
        => Assert.Equal(Outcome.Refused, Read(Large(flags, 4, 0, "tiny"u8), allowLarge: false));

    [Fact]
    public void A_large_frame_with_a_small_64_bit_length_is_read_where_large_frames_are_allowed()
    {
        Outcome outcome = Read(Large(0x05, 4, 0, "tiny"u8), null, true, out ReadOnlyMemory<byte> payload, out _);

        Assert.Equal((Outcome.Payload, "tiny"), (outcome, Encoding.UTF8.GetString(payload.Span)));
    }

    [Fact]
    public void A_negative_cap_refuses_every_non_empty_frame_rather_than_wrapping_to_a_huge_one()
        => Assert.Equal(Outcome.Refused, Read(Small(0x01, 1, 0, "x"u8), cap: -1));

    // ---- compression ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_compressed_frame_whose_body_is_not_zlib_is_invalid_data()
    {
        byte[] garbage = [0x78, 0x9C, 0xFF, 0xFF, 0x00, 0x13, 0x37, 0xDE, 0xAD, 0xBE, 0xEF];

        Assert.Equal(Outcome.Refused, Read(Small(0x03, (uint)garbage.Length, 10, garbage)));
    }

    [Fact]
    public void A_compressed_frame_with_a_wrong_adler32_is_refused_as_zlib_refuses_it()
    {
        byte[] body = Deflate("hello, zabbix"u8);
        body[^1] ^= 0xFF;

        Assert.Equal(Outcome.Refused, Read(Small(0x03, (uint)body.Length, 13, body)));
    }

    // Upstream: zlib's uncompress() stops at the end of the zlib stream and ignores what follows (uncompr.c), and comms.c
    // only checks the inflated size. The reader here does the same.
    [Fact]
    public void A_zlib_body_followed_by_bytes_inflates_to_its_payload_as_upstream_s_uncompress_does()
    {
        byte[] body = [.. Deflate("hello, zabbix"u8), .. "trailing"u8];

        Outcome outcome = Read(Small(0x03, (uint)body.Length, 13, body), null, false, out ReadOnlyMemory<byte> payload, out _);

        Assert.Equal((Outcome.Payload, "hello, zabbix"), (outcome, Encoding.UTF8.GetString(payload.Span)));
    }

    // zlib's uncompress() answers Z_DATA_ERROR for a stream that ends before its adler32 trailer (uncompr.c,
    // "incomplete"), so upstream refuses the frame ("cannot uncompress data"). ZLibStream returns 0 at the end of its
    // input without complaint, so without a check of its own a body cut after its last deflate block (the payload
    // complete, the checksum gone) would be accepted here.
    [Fact]
    public void A_zlib_body_cut_before_its_checksum_is_refused_as_upstream_s_uncompress_refuses_it()
    {
        byte[] body = Deflate("hello, zabbix"u8)[..^4];

        Assert.Equal(Outcome.Refused, Read(Small(0x03, (uint)body.Length, 13, body)));
    }

    // An empty body under the compressed flag is not a zlib stream (uncompress: Z_DATA_ERROR), so upstream
    // refuses it. The 13-byte frame ZBXD 03 00000000 00000000 must be refused, not escape TryRead as
    // ArgumentOutOfRangeException ("position", from SequencePipeReader.AdvanceTo under PipeReaderStream.Read under
    // DeflateStream.ReadCore): that is not InvalidDataException, so ZabbixFrames.IsExchangeFailure would not know it and
    // the sender's DeliverAsync would let it out.
    [Fact]
    public void A_compressed_frame_with_an_empty_body_is_refused_as_upstream_s_uncompress_refuses_it()
        => Assert.Equal(Outcome.Refused, Read(Small(0x03, 0, 0, default)));

    // The zlib stream of an empty payload, reserved 0: upstream inflates it to 0 bytes and accepts it (out_size
    // == reserved), and so does this reader. The body is zlib's own compress("") (78 9C 03 00 00 00
    // 00 01, the header, an empty final block and the Adler-32 of nothing): .NET's ZLibStream writes no bytes at all for an
    // empty input (measured on 10.0.11), so Deflate(default) is the empty body of the test above, not a zlib stream.
    [Fact]
    public void A_compressed_frame_that_inflates_to_nothing_and_says_so_is_an_empty_payload()
    {
        byte[] body = [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01];

        Outcome outcome = Read(Small(0x03, (uint)body.Length, 0, body), null, false, out ReadOnlyMemory<byte> payload, out _);

        Assert.Equal((Outcome.Payload, 0), (outcome, payload.Length));
    }

    [Fact]
    public void A_compressed_frame_with_reserved_zero_that_inflates_to_something_is_refused()
    {
        byte[] body = Deflate("x"u8);

        Assert.Equal(Outcome.Refused, Read(Small(0x03, (uint)body.Length, 0, body)));
    }

    [Fact]
    public void A_bomb_that_inflates_far_past_its_reserved_length_stops_one_byte_past_it()
    {
        byte[] body = Deflate(new byte[8 * 1024 * 1024]);                                            // ~8 KiB of zlib, 8 MiB of zeros
        byte[] frame = Small(0x03, (uint)body.Length, 100, body);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Outcome outcome = Read(frame);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Outcome.Refused, outcome);
        Assert.True(allocated < 256 * 1024, $"allocated {allocated} bytes refusing a bomb that declared 100");
    }

    [Fact]
    public void A_large_frame_declaring_the_largest_array_but_delivering_a_tiny_body_costs_what_it_delivers()
    {
        byte[] body = Deflate("tiny"u8);
        byte[] frame = Large(0x07, (ulong)body.Length, (ulong)Array.MaxLength, body);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Outcome outcome = Read(frame, allowLarge: true);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Outcome.Refused, outcome);                                                        // inflated 4, declared ~2 GiB
        Assert.True(allocated < 256 * 1024, $"allocated {allocated} bytes for a {frame.Length}-byte frame");
    }

    // An honest compressed frame (it inflates to exactly `reserved`) costs about its inflated size. An inflate buffer
    // that doubles from 16 KiB, plus a one-byte probe past `reserved` (to tell a longer payload) that asks for one more
    // byte when the buffer is exactly full, doubles it again; a payload handed back as a slice of that doubled array keeps
    // twice `reserved` alive with it: about 16.8 MB allocated at reserved = 4 MiB, and at the 1 GiB client cap a 2 GiB
    // array retained for a 1 GiB payload. A probe into a one-byte scratch span keeps it near 1×.
    [Fact]
    public void An_honest_compressed_frame_costs_about_its_inflated_size_not_four_times_it()
    {
        const int Reserved = 4 * 1024 * 1024;
        byte[] body = Deflate(new byte[Reserved]);
        byte[] frame = Small(0x03, (uint)body.Length, Reserved, body);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Outcome outcome = Read(frame, null, false, out ReadOnlyMemory<byte> payload, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal((Outcome.Payload, Reserved), (outcome, payload.Length));
        Assert.True(allocated < 2L * Reserved, $"allocated {allocated} bytes to inflate {Reserved}");
    }
}
