// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Runtime.InteropServices;

namespace Community.Zabbix.Protocol;

/// <summary>
/// Reads and writes the Zabbix protocol header shared by the trapper, agent and proxy protocols: <c>ZBXD</c>, one flags byte
/// (<c>0x01</c> the protocol, <c>0x02</c> zlib-compressed, <c>0x04</c> large), the payload length and the
/// <c>reserved</c> length (the inflated size of a compressed payload), unsigned 32-bit each or 64-bit under the large
/// flag, then the payload. Framing is done over <see cref="System.IO.Pipelines"/>: a frame is read from a
/// <see cref="ReadOnlySequence{T}"/> without copying the header and written to an <see cref="IBufferWriter{T}"/>: a plain
/// payload without an intermediate array, a compressed one deflated into a scratch buffer first, because the header
/// declares its length and the frame keeps the plain payload when zlib does not make it smaller. A frame is refused as upstream refuses it (<c>src/libs/zbxcomms/comms.c</c>,
/// <c>zbx_tcp_recv_context</c>, 7.0/7.4/8.0 alike): a flags byte without <c>0x01</c> or above what the reader allows, a
/// declared or <c>reserved</c> length past the cap (both are checked, compressed or not), and a compressed payload
/// that is not one whole zlib stream inflating to exactly <c>reserved</c> bytes (<c>zbx_uncompress</c>, zlib's
/// <c>uncompress</c>). The inflate buffer grows as bytes arrive, so a small frame that declares a large size costs nothing
/// until it delivers.
/// </summary>
internal static class ZabbixHeader
{
    private const byte FlagProtocol = 0x01;
    private const byte FlagCompressed = 0x02;
    private const byte FlagLarge = 0x04;
    private const int SmallHeader = 13;
    private const int LargeHeader = 21;
    private const int InflateChunk = 16 * 1024;
    private const int ZlibOverhead = 2 + 4;                                                   // RFC 1950: header, Adler-32

    /// <summary>
    /// The most a frame may carry, declared or inflated: 1 GiB, upstream's <c>ZBX_MAX_RECV_DATA_SIZE</c>
    /// (<c>include/zbxcommon.h</c>: 7.0:527, 7.4:536, 8.0:564). Upstream has two other limits, both on the receiving
    /// side of a listener: 2 KiB (<c>ZBX_MAX_RECV_2KB_DATA_SIZE</c>, 7.4 and 8.0) for an unencrypted peer let through
    /// only for a stats request, and 16 GiB (<c>ZBX_MAX_RECV_LARGE_DATA_SIZE</c> on 64-bit) for a large frame, which
    /// only a proxy's configuration receive accepts (<c>zbx_tcp_recv_large</c>). A payload here is one
    /// <see cref="ReadOnlyMemory{T}"/>, so a large frame is also bounded by <see cref="Array.MaxLength"/>.
    /// </summary>
    public const long MaxPayload = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// The most a frame may carry where a large frame is accepted: 16 GiB, upstream's <c>ZBX_MAX_RECV_LARGE_DATA_SIZE</c>
    /// on a 64-bit build (<c>include/zbxcommon.h</c>), which <c>zbx_tcp_recv_context_init</c> takes as the cap whenever the
    /// receive carries <c>ZBX_TCP_LARGE</c> and the socket has no explicit limit. A payload here is one
    /// <see cref="ReadOnlyMemory{T}"/>, so the effective cap is also <see cref="Array.MaxLength"/>.
    /// </summary>
    public const long MaxLargePayload = 16L * 1024 * 1024 * 1024;

    private static ReadOnlySpan<byte> Magic => "ZBXD"u8;

    /// <summary>Frames <paramref name="payload"/> into <paramref name="writer"/>, compressing when that makes it smaller.</summary>
    /// <param name="writer">Where the frame goes.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="compress">Whether to try zlib.</param>
    public static void Write(IBufferWriter<byte> writer, ReadOnlySpan<byte> payload, bool compress = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (compress && payload.Length > 0)
        {
            var deflated = new ArrayBufferWriter<byte>();
            using (var zlib = new ZLibStream(new BufferWriterStream(deflated), CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(payload);
            if (deflated.WrittenCount < payload.Length)
            {
                Span<byte> header = writer.GetSpan(SmallHeader);
                Magic.CopyTo(header);
                header[4] = FlagProtocol | FlagCompressed;
                BinaryPrimitives.WriteUInt32LittleEndian(header[5..], (uint)deflated.WrittenCount);
                BinaryPrimitives.WriteUInt32LittleEndian(header[9..], (uint)payload.Length);
                writer.Advance(SmallHeader);
                writer.Write(deflated.WrittenSpan);
                return;
            }
        }

        Span<byte> plain = writer.GetSpan(SmallHeader);
        Magic.CopyTo(plain);
        plain[4] = FlagProtocol;
        BinaryPrimitives.WriteUInt32LittleEndian(plain[5..], (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(plain[9..], 0);
        writer.Advance(SmallHeader);
        writer.Write(payload);
    }

    /// <summary>
    /// Reads one frame from the front of <paramref name="buffer"/>. Returns <see langword="false"/> when more bytes are
    /// needed; on <see langword="true"/> the frame is sliced off <paramref name="buffer"/> and the payload, inflated if it
    /// was compressed, is in <paramref name="payload"/> (always a copy: see the remark at the copy).
    /// </summary>
    /// <param name="buffer">The bytes read so far; advanced past the frame on success.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="maxPayload">The cap on the declared and the <c>reserved</c> length; upstream's for the mode when
    /// <see langword="null"/>: <see cref="MaxPayload"/>, or <see cref="MaxLargePayload"/> with <paramref name="allowLarge"/>
    /// (an explicit cap wins, as a socket's <c>max_len_limit</c> does upstream).</param>
    /// <param name="allowLarge">Whether a large (<c>0x04</c>, 64-bit length) frame is accepted, and the large cap applies;
    /// upstream receives this way only where a proxy receives its configuration (<c>zbx_tcp_recv_large</c>).</param>
    /// <exception cref="InvalidDataException">The bytes are not a Zabbix frame, the flags are not ones the reader allows,
    /// a length exceeds the cap, or a compressed payload is not a whole zlib stream of its declared size.</exception>
    public static bool TryRead(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> payload, long? maxPayload = null, bool allowLarge = false)
        => TryReadFrame(ref buffer, out payload, maxPayload, allowLarge, out _, out _);

    /// <summary>A frame's header, once its bytes are in: how long the header is, the body's length, and how to read it.</summary>
    private readonly record struct FrameShape(int HeaderLength, long Length, bool Compressed, int Reserved);

    // The one parse path. `needed` is how many bytes the frame at the front takes in all, as far as the bytes so far tell:
    // 5 until the flags are in, the header's length until the header is, then the header and the body.
    private static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> payload, long? maxPayload, bool allowLarge, out long needed, out FrameShape? shape)
    {
        payload = default;
        shape = null;
        needed = 5;
        if (buffer.Length < 5) return false;

        Span<byte> header = stackalloc byte[LargeHeader];
        buffer.Slice(0, 5).CopyTo(header);
        if (!header[..4].SequenceEqual(Magic)) throw new InvalidDataException("The bytes are not a Zabbix (ZBXD) frame.");

        // comms.c: `0 == (version & ZBX_TCP_PROTOCOL) || version > (ZBX_TCP_PROTOCOL | ZBX_TCP_COMPRESS | flags)`.
        byte flags = header[4];
        byte highest = (byte)(FlagProtocol | FlagCompressed | (allowLarge ? FlagLarge : 0));
        if ((flags & FlagProtocol) == 0 || flags > highest)
            throw new InvalidDataException($"The frame's flags 0x{flags:x2} are not a protocol version this reader accepts (at most 0x{highest:x2}).");

        bool large = (flags & FlagLarge) != 0;
        int headerLength = large ? LargeHeader : SmallHeader;
        needed = headerLength;
        if (buffer.Length < headerLength) return false;
        buffer.Slice(0, headerLength).CopyTo(header);

        ulong length = large ? BinaryPrimitives.ReadUInt64LittleEndian(header[5..]) : BinaryPrimitives.ReadUInt32LittleEndian(header[5..]);
        ulong reserved = large ? BinaryPrimitives.ReadUInt64LittleEndian(header[13..]) : BinaryPrimitives.ReadUInt32LittleEndian(header[9..]);
        ulong cap = (ulong)Math.Min(Math.Max(maxPayload ?? (allowLarge ? MaxLargePayload : MaxPayload), 0), Array.MaxLength);
        if (length > cap) throw new InvalidDataException($"The frame declares {length} bytes; the cap is {cap}.");
        if (reserved > cap) throw new InvalidDataException($"The frame's reserved length is {reserved} bytes; the cap is {cap}.");
        needed = headerLength + (long)length;
        shape = new FrameShape(headerLength, (long)length, (flags & FlagCompressed) != 0, (int)reserved);
        if (buffer.Length < needed) return false;

        ReadOnlySequence<byte> body = buffer.Slice(headerLength, (long)length);
        payload = (flags & FlagCompressed) != 0
            ? Inflate(body, (int)reserved)
            // Always a copy: the caller advances the pipe past this frame, which hands its segments back to the pool, so a
            // slice of them (body.First) would be read after release and overwritten by the next connection's bytes.
            : body.ToArray();
        buffer = buffer.Slice(needed);
        return true;
    }

    // Inflates as upstream's zbx_uncompress (zlib's uncompress() into exactly `reserved` bytes) accepts: a whole zlib stream
    // that inflates to exactly `reserved` bytes; bytes after the stream's end are ignored, as uncompress() ignores them. The
    // buffer is never sized from the header alone: it starts at a chunk and grows fourfold, capped at `reserved`, so a frame
    // that declares a large `reserved` and delivers little costs about what it delivers, and an honest one costs its
    // payload plus the smaller steps before it (1.33x on a power of four, under 2x at worst). The byte past `reserved` that
    // tells a longer payload is probed into one stack byte, so an honest payload is never doubled for it.
    private static ReadOnlyMemory<byte> Inflate(ReadOnlySequence<byte> body, int reserved)
    {
        // uncompress() answers Z_DATA_ERROR for a body shorter than a zlib stream can be (the 2-byte header and the
        // 4-byte trailer), an empty one included.
        if (body.Length < ZlibOverhead) throw new InvalidDataException($"The frame's compressed body is {body.Length} bytes; a zlib stream is at least {ZlibOverhead}.");
        byte[] inflated = new byte[Math.Min(reserved, InflateChunk)];
        int written = 0;
        using var source = CompressedBody.Over(body);
        using (var zlib = new ZLibStream(source, CompressionMode.Decompress, leaveOpen: true))
        {
            while (true)
            {
                if (written == reserved)
                {
                    Span<byte> past = stackalloc byte[1];
                    if (zlib.Read(past) != 0) throw new InvalidDataException($"The frame inflates past its declared {reserved} bytes.");
                    break;
                }
                if (written == inflated.Length) Array.Resize(ref inflated, (int)Math.Min(4L * inflated.Length, reserved));
                int read = zlib.Read(inflated.AsSpan(written));
                if (read == 0) break;
                written += read;
            }
        }
        // uncompress() answers Z_DATA_ERROR for a stream that ends before its end (its last block or its Adler-32 trailer);
        // ZLibStream ends quietly at the end of its input instead, having asked the input for more.
        if (source.Exhausted) throw new InvalidDataException("The frame's zlib stream is cut short before its end (its last block or its checksum).");
        if (written != reserved)
            throw new InvalidDataException($"The frame inflated to {written} bytes; it declared {reserved}.");
        return inflated.AsMemory(0, written);
    }

    /// <summary>
    /// A compressed body as the <see cref="Stream"/> <see cref="ZLibStream"/> reads, which says whether it was asked for
    /// bytes past its end (a <see cref="MemoryStream"/> with two reads overridden). ZLibStream reports no end of stream; it
    /// stops reading its input once the zlib stream has ended and asks for more only while it has not, so an ask past the
    /// body's end is a stream cut short (this holds for whole bodies, bodies cut 1 and 4 bytes short, bodies followed by
    /// bytes, and bodies of exactly 8, 16, 32 and 64 KiB). <c>PipeReader.Create(ReadOnlySequence).AsStream()</c> is not
    /// used: under DeflateStream it throws <see cref="ArgumentOutOfRangeException"/> (<c>SequencePipeReader.AdvanceTo</c>)
    /// on an empty body.
    /// </summary>
    private sealed class CompressedBody : MemoryStream
    {
        private CompressedBody(byte[] buffer, int index, int count)
            : base(buffer, index, count, writable: false)
        {
        }

        /// <summary>Whether a read found nothing left.</summary>
        public bool Exhausted { get; private set; }

        /// <summary>The body's own array when it is one array segment, a copy of the compressed bytes otherwise.</summary>
        public static CompressedBody Over(ReadOnlySequence<byte> body)
        {
            if (body.IsSingleSegment && MemoryMarshal.TryGetArray(body.First, out ArraySegment<byte> segment) && segment.Array is { } array)
                return new CompressedBody(array, segment.Offset, segment.Count);
            byte[] copy = body.ToArray();
            return new CompressedBody(copy, 0, copy.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => Saw(base.Read(buffer, offset, count), count);

        public override int Read(Span<byte> buffer) => Saw(base.Read(buffer), buffer.Length);

        private int Saw(int read, int asked)
        {
            if (read == 0 && asked > 0) Exhausted = true;
            return read;
        }
    }

    /// <summary>Frames <paramref name="payload"/> into <paramref name="writer"/> and flushes.</summary>
    /// <param name="writer">The connection.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="compress">Whether to try zlib.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async ValueTask WriteAsync(PipeWriter writer, ReadOnlyMemory<byte> payload, bool compress = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Write(writer, payload.Span, compress);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame from <paramref name="reader"/>.</summary>
    /// <param name="reader">The connection.</param>
    /// <param name="maxPayload">The cap on the declared and the <c>reserved</c> length; upstream's for the mode when
    /// <see langword="null"/> (see <see cref="TryRead"/>).</param>
    /// <param name="allowLarge">Whether a large frame is accepted, and the large cap applies (<c>zbx_tcp_recv_large</c>).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="InvalidDataException">The bytes are not a Zabbix frame, the frame is refused (see
    /// <see cref="TryRead"/>), or the connection ended mid-frame.</exception>
    public static async ValueTask<ReadOnlyMemory<byte>> ReadAsync(PipeReader reader, long? maxPayload = null, bool allowLarge = false, CancellationToken cancellationToken = default)
        => await TryReadAsync(reader, maxPayload, allowLarge, cancellationToken).ConfigureAwait(false) ?? throw NoReply();

    /// <summary>
    /// Reads one frame from <paramref name="reader"/>, or answers <see langword="null"/> when the connection ended before
    /// one byte of it arrived: the Try form of <see cref="ReadAsync(PipeReader, long?, bool, CancellationToken)"/>, for a
    /// read where the peer closing is an expected answer (after a notification).
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a Zabbix frame, the frame is refused, or the connection ended mid-frame.</exception>
    internal static async ValueTask<ReadOnlyMemory<byte>?> TryReadAsync(PipeReader reader, long? maxPayload, bool allowLarge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (TryReadFrame(ref buffer, out ReadOnlyMemory<byte> payload, maxPayload, allowLarge, out _, out FrameShape? shape))
            {
                reader.AdvanceTo(buffer.Start);
                return payload;
            }
            if (shape is { Length: > ReadSegment } large)
            {
                reader.AdvanceTo(buffer.GetPosition(large.HeaderLength));
                return await ReadBodyAsync(reader, large, cancellationToken).ConfigureAwait(false);
            }
            reader.AdvanceTo(buffer.Start, buffer.End);                                   // consumed nothing, examined everything
            if (result.IsCompleted) return buffer.IsEmpty ? null : throw new InvalidDataException("The connection ended before a whole frame arrived.");
        }
    }

    // A body larger than one read segment is moved out of the pipe as it arrives, into an array the caller then owns, so
    // the pipe hands its segments back as they are copied instead of holding the whole body beside the copy (twice the
    // frame at its peak). The array is never sized from the header alone: it starts at a segment and grows fourfold, capped
    // at the declared length, as Inflate's does, so a peer that declares much and delivers little costs what it delivers.
    private static async ValueTask<ReadOnlyMemory<byte>> ReadBodyAsync(PipeReader reader, FrameShape shape, CancellationToken cancellationToken)
    {
        byte[] body = new byte[(int)Math.Min(shape.Length, ReadSegment)];
        int written = 0;
        while (written < shape.Length)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> arrived = result.Buffer.Slice(0, Math.Min(result.Buffer.Length, shape.Length - written));
            while (written + arrived.Length > body.Length) Array.Resize(ref body, (int)Math.Min(4L * body.Length, shape.Length));
            arrived.CopyTo(body.AsSpan(written));
            written += (int)arrived.Length;
            reader.AdvanceTo(arrived.End);
            if (written < shape.Length && result.IsCompleted) throw new InvalidDataException("The connection ended before a whole frame arrived.");
        }
        return shape.Compressed ? Inflate(new ReadOnlySequence<byte>(body), shape.Reserved) : body;
    }

    /// <summary>
    /// How much a frame read asks of its stream at a time: a large frame (a proxy's configuration) arrives in a few large
    /// reads rather than one per 4 KiB pipe segment, while a small one asks only for what its header declares.
    /// </summary>
    internal const int ReadSegment = 64 * 1024;

    /// <summary>
    /// Whether <paramref name="failure"/> is a read that ended before one byte of a reply arrived: the peer closed the
    /// connection without answering. Upstream closes this way on purpose when it refuses without saying why: a server asked
    /// for <c>proxy config</c> by a proxy it does not know, or from an address or encryption that proxy is not allowed
    /// (<c>zbx_send_proxyconfig</c>, <c>src/zabbix_server/proxyconfigread/proxyconfigread.c</c>: logged there, nothing
    /// sent). Still an <see cref="InvalidDataException"/> (a frame was expected and none came), which is sealed, so the
    /// mark is in its <see cref="Exception.Data"/>.
    /// </summary>
    /// <param name="failure">A failure of <see cref="ReadAsync(PipeReader, long?, bool, CancellationToken)"/> or <see cref="ReadAsync(Stream, CancellationToken)"/>, or anything else.</param>
    public static bool IsNoReply(Exception failure) => failure is InvalidDataException && failure.Data[NoReplyMark] is true;

    private const string NoReplyMark = "Zabbix.NoReply";

    private static InvalidDataException NoReply()
    {
        var failure = new InvalidDataException("The peer closed the connection without answering.");
        failure.Data[NoReplyMark] = true;
        return failure;
    }

    /// <summary>Frames <paramref name="payload"/> and writes it to <paramref name="stream"/>.</summary>
    /// <param name="stream">The connection.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var frame = new ArrayBufferWriter<byte>(SmallHeader + payload.Length);
        Write(frame, payload.Span);
        await stream.WriteAsync(frame.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one frame from <paramref name="stream"/>, and not a byte past it: the header, then exactly the body it
    /// declares, so a frame the peer sent right behind it stays on the stream for the next call. The bytes are held in a
    /// <see cref="Pipe"/>'s pooled segments as they arrive (never a buffer sized from the header) and parsed by
    /// <see cref="TryRead"/>, as a connection's pipe is.
    /// </summary>
    /// <param name="stream">The connection.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="InvalidDataException">The bytes are not a Zabbix frame, the frame is refused (see
    /// <see cref="TryRead"/>), or the stream ended mid-frame.</exception>
    public static async Task<ReadOnlyMemory<byte>> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var frame = new Pipe(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));   // never pauses: one frame, read whole
        try
        {
            long arrived = 0;
            long needed = 5;
            while (true)
            {
                while (arrived < needed)
                {
                    Memory<byte> into = frame.Writer.GetMemory((int)Math.Min(needed - arrived, ReadSegment));
                    int read = await stream.ReadAsync(into[..(int)Math.Min(into.Length, needed - arrived)], cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw arrived == 0 ? NoReply() : new InvalidDataException("The connection ended before a whole frame arrived.");
                    frame.Writer.Advance(read);
                    arrived += read;
                }
                await frame.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                frame.Reader.TryRead(out ReadResult result);
                ReadOnlySequence<byte> buffer = result.Buffer;
                bool whole = TryReadFrame(ref buffer, out ReadOnlyMemory<byte> payload, null, false, out needed, out _);
                frame.Reader.AdvanceTo(buffer.Start, buffer.End);
                if (whole) return payload;
            }
        }
        finally
        {
            await frame.Writer.CompleteAsync().ConfigureAwait(false);
            await frame.Reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    // A write-only Stream over an IBufferWriter, so ZLibStream deflates straight into the frame buffer. Owned because
    // ZLibStream takes only a Stream and the .NET 10 shared framework has no Stream over an IBufferWriter<byte>;
    // PipeWriter.AsStream needs a PipeWriter, which the frame buffer is not.
    private sealed class BufferWriterStream(IBufferWriter<byte> writer) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => writer.Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => writer.Write(buffer);
    }
}
