// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Community.Zabbix.Protocol;

namespace Community.Zabbix.Tests;

/// <summary>
/// Allocation probes on the hot paths: allocation-free where the platform is. Each probe measures the bytes the current thread allocates across one call after a warm-up, and holds it
/// to a bound derived from what the call must allocate (its payload), so a copy, a boxing or a per-call buffer that creeps
/// in fails here instead of in a profile. The bounds were measured on .NET 10 (x64) and leave room only for the runtime's
/// own bookkeeping.
/// </summary>
public sealed class AdversarialAllocationTests
{
    private const int Payload = 64 * 1024;

    [Fact]
    public void Framing_a_plain_payload_into_a_ready_buffer_allocates_nothing()
    {
        byte[] payload = new byte[Payload];
        var buffer = new ArrayBufferWriter<byte>(Payload + 64);
        ZabbixHeader.Write(buffer, payload);                                                // warm-up
        buffer.ResetWrittenCount();

        long allocated = Measure(() => ZabbixHeader.Write(buffer, payload));

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Reading_a_plain_frame_copies_its_payload_once_and_nothing_else()
    {
        byte[] frame = Frame(compress: false);
        Read(frame);                                                                        // warm-up

        long allocated = Measure(() => Read(frame));

        Assert.InRange(allocated, Payload, Payload + 1024);
    }

    /// <summary>
    /// Inflating a frame allocates at most twice its inflated size plus the inflater. <c>ZabbixHeader.Inflate</c> starts its
    /// <see cref="ArrayBufferWriter{T}"/> at 16 KiB and doubles (16+32+64 KiB for a 64 KiB frame); a "one byte past
    /// <c>reserved</c>" overrun probe that asks the buffer for room would double it once more (128 KiB, about 3.8x in all)
    /// for a byte that is never there. Once <c>reserved</c> bytes are in, the overrun is probed into a one-byte stack span;
    /// the growth policy (never sized from the header) stays.
    /// </summary>
    [Fact]
    public void Reading_a_compressed_frame_allocates_at_most_its_inflated_size_twice_plus_the_inflater()
    {
        byte[] frame = Frame(compress: true);
        Read(frame);                                                                        // warm-up

        long allocated = Measure(() => Read(frame));

        Assert.InRange(allocated, Payload, (2 * Payload) + (32 * 1024));
    }

    /// <summary>
    /// Parsing validates and keeps the text: a key with brackets costs one 24-byte holder for the view it reads on first
    /// access, and its name and parameters are not built until then (building them eagerly would cost 410 B per key).
    /// </summary>
    [Fact]
    public void Parsing_an_item_key_allocates_one_holder_and_no_parts()
    {
        const string key = "vfs.fs.size[/var/lib,pfree]";
        _ = ZabbixItemKey.Parse(key, CultureInfo.InvariantCulture);                         // warm-up

        long allocated = Measure(() => _ = ZabbixItemKey.Parse(key, CultureInfo.InvariantCulture));

        Assert.InRange(allocated, 1, 24);
    }

    [Fact]
    public void Parsing_a_bare_item_key_name_allocates_nothing()
    {
        const string key = "system.uptime";
        _ = ZabbixItemKey.Parse(key, CultureInfo.InvariantCulture);                         // warm-up

        long allocated = Measure(() => _ = ZabbixItemKey.Parse(key, CultureInfo.InvariantCulture).Name);

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_parsed_key_reads_its_parts_on_first_access_and_keeps_them()
    {
        ZabbixItemKey key = ZabbixItemKey.Parse("vfs.fs.size[\"/var lib\",[a,b],pfree]", CultureInfo.InvariantCulture);

        Assert.Equal("vfs.fs.size", key.Name);
        Assert.Equal(["/var lib", "a,b", "pfree"], key.Parameters);
        Assert.Equal(ZabbixItemKeyParameterType.Array, key.GetParameterType(1));
        Assert.Same(key.Parameters, key.Parameters);
        Assert.Same(key.Name, key.Name);
    }

    [Fact]
    public void Broadcasting_a_value_to_attached_readers_allocates_nothing()
    {
        var fanOut = new ZabbixFanOut<object>();
        var options = new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true };
        for (int i = 0; i < 3; i++) fanOut.Attach(Channel.CreateBounded<object>(options));
        object value = new();
        for (int i = 0; i < 8; i++) fanOut.Write(value);                                    // warm-up: every channel full

        long allocated = Measure(() => fanOut.Write(value));

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_detached_reader_receives_nothing_more_and_the_others_still_do()
    {
        var fanOut = new ZabbixFanOut<int>();
        Channel<int> kept = Channel.CreateUnbounded<int>(), left = Channel.CreateUnbounded<int>();
        fanOut.Attach(kept);
        fanOut.Attach(left);
        fanOut.Write(1);
        fanOut.Detach(left);
        fanOut.Write(2);
        fanOut.Complete();

        Assert.Equal(1, fanOut.Count);
        Assert.True(kept.Reader.TryRead(out int first));
        Assert.True(kept.Reader.TryRead(out int second));
        Assert.Equal((1, 2), (first, second));
        Assert.True(kept.Reader.Completion.IsCompleted);
        Assert.Equal(1, left.Reader.Count);
        Assert.False(left.Reader.Completion.IsCompleted);
    }

    /// <summary>
    /// A send serializes each value once into one buffer laid out as the frame, which a one-frame call sends as it is, so
    /// it allocates that buffer and each value's wire item: 211 B per value for 93 B of JSON, against 4.5 times the JSON
    /// through a byte array per item and a growing frame buffer.
    /// </summary>
    [Fact]
    public async Task Sending_values_allocates_less_than_two_and_a_half_times_their_json()
    {
        const int Count = 250;
        byte[] reply = Encoding.UTF8.GetBytes("{\"response\":\"success\",\"info\":\"processed: 250; failed: 0; total: 250; seconds spent: 0.001\"}");
        int json = 0;
        ValueTask<ReadOnlyMemory<byte>> Answer(ReadOnlyMemory<byte> payload, CancellationToken _)
        {
            json = payload.Length;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(reply);
        }
        using var sender = new ZabbixSender(Answer, "test");
        ZabbixValue[] values = [.. Enumerable.Range(0, Count).Select(i => new ZabbixValue("web-01", ZabbixItemKey.Parse("vfs.fs.size[/var/lib,pfree]", CultureInfo.InvariantCulture), i.ToString(CultureInfo.InvariantCulture), new ZabbixClock(1_700_000_000 + i, 0)))];
        _ = await sender.SendAsync(values, TestContext.Current.CancellationToken);           // warm-up

        long before = GC.GetAllocatedBytesForCurrentThread();
        Task<ZabbixSenderResponse> sending = sender.SendAsync(values, TestContext.Current.CancellationToken);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _ = await sending;

        Assert.True(allocated * 2 < 5L * json, $"{allocated / Count} B per value for {json / Count} B of JSON per value");
    }

    /// <summary>
    /// A body larger than one read segment leaves the pipe as it arrives, so the pipe never holds the frame beside its
    /// copy: a 1 MiB frame is read through a pipe that pauses its writer at 128 KiB, which a reader that waits for the whole
    /// frame in the pipe would never finish.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_large_frame_is_read_through_a_pipe_that_never_holds_it_whole(bool compress)
    {
        const int Large = 1024 * 1024;
        byte[] payload = new byte[Large];
        RandomNumberGenerator.Fill(payload.AsSpan(0, Large / 2));                              // half noise, so a compressed body is large too
        var framed = new ArrayBufferWriter<byte>();
        ZabbixHeader.Write(framed, payload, compress);
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 128 * 1024, resumeWriterThreshold: 64 * 1024, useSynchronizationContext: false));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Task writing = WriteAsync();

        ReadOnlyMemory<byte> read = await ZabbixHeader.ReadAsync(pipe.Reader, maxPayload: 2 * Large, cancellationToken: cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await writing;

        Assert.True(read.Span.SequenceEqual(payload));

        async Task WriteAsync()
        {
            for (int at = 0; at < framed.WrittenCount; at += 16 * 1024)
                _ = await pipe.Writer.WriteAsync(framed.WrittenMemory[at..Math.Min(framed.WrittenCount, at + (16 * 1024))], cancellationToken);
            await pipe.Writer.CompleteAsync();
        }
    }

    [Fact]
    public async Task A_large_frame_cut_short_is_refused()
    {
        var framed = new ArrayBufferWriter<byte>();
        ZabbixHeader.Write(framed, new byte[256 * 1024]);
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        _ = await pipe.Writer.WriteAsync(framed.WrittenMemory[..(128 * 1024)], TestContext.Current.CancellationToken);
        await pipe.Writer.CompleteAsync();

        await Assert.ThrowsAsync<InvalidDataException>(async () => await ZabbixHeader.ReadAsync(pipe.Reader, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static byte[] Frame(bool compress)
    {
        byte[] payload = new byte[Payload];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + (i % 7));
        var buffer = new ArrayBufferWriter<byte>();
        ZabbixHeader.Write(buffer, payload, compress);
        return buffer.WrittenSpan.ToArray();
    }

    private static void Read(byte[] frame)
    {
        var sequence = new ReadOnlySequence<byte>(frame);
        Assert.True(ZabbixHeader.TryRead(ref sequence, out ReadOnlyMemory<byte> payload));
        Assert.Equal(Payload, payload.Length);
    }

    private static long Measure(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
