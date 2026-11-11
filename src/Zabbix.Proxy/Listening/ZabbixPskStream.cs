// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using Org.BouncyCastle.Tls;

namespace Community.Zabbix.Listening;

/// <summary>
/// A <see cref="Stream"/> over a BouncyCastle server in non-blocking mode and the connection's pipes: a read offers the
/// pipe's bytes to the protocol and returns what it decrypted, a write hands application data to the protocol and drains
/// its records to the pipe. Every wait is a pipe await, so the caller's token cancels it.
/// </summary>
/// <remarks>
/// Decrypted bytes go straight into the caller's array when the buffer has one, else through one read buffer the stream
/// keeps; the records a drain writes are a rented array. A zero-length read waits until decrypted bytes are available or
/// the session ends and takes nothing, as <see cref="System.Net.Security.SslStream"/> does, so a caller can wait for data
/// without a buffer; 0 from a non-empty buffer is the end of the stream.
/// </remarks>
internal sealed class ZabbixPskStream(TlsServerProtocol protocol, IDuplexPipe transport) : Stream
{
    // One TLS record's plaintext at most: what one ReadInput into a buffer without an array can take.
    private const int ReadSize = 16 * 1024;

    private byte[]? _input;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (protocol.GetAvailableInputBytes() > 0) return buffer.IsEmpty ? 0 : TakeInput(buffer);
            if (protocol.IsClosed) return 0;
            ReadResult result = await transport.Input.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (result.Buffer.IsEmpty && result.IsCompleted) return 0;
            Offer(protocol, result.Buffer);
            transport.Input.AdvanceTo(result.Buffer.End);
            await DrainAsync(protocol, transport.Output, cancellationToken).ConfigureAwait(false);   // alerts the offer produced
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        protocol.WriteApplicationData(buffer.Span);
        await DrainAsync(protocol, transport.Output, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) => transport.Output.FlushAsync(cancellationToken).AsTask();

    /// <summary>Does nothing: the stream buffers nothing, every write hands its records to the pipe and flushes it before it returns.</summary>
    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("The PSK stream is asynchronous only.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The PSK stream is asynchronous only.");

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (!protocol.IsClosed)
        {
            protocol.Close();                                                                  // close_notify, as upstream's SSL_shutdown
            try { await DrainAsync(protocol, transport.Output, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception failure) when (failure is IOException or InvalidOperationException) { /* the peer is gone; nothing to tell it */ }
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the session without sending <c>close_notify</c>: sending it is a write, and the stream does no synchronous I/O.
    /// The listener closes the connection after, which the peer reads as the end of the session too.
    /// <see cref="DisposeAsync"/> sends <c>close_notify</c> first.
    /// </summary>
    /// <param name="disposing">Whether the call is <see cref="Stream.Dispose()"/>'s.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !protocol.IsClosed) protocol.Close();
        base.Dispose(disposing);
    }

    /// <summary>Offers every segment of <paramref name="buffer"/> to the protocol.</summary>
    internal static void Offer(TlsProtocol protocol, ReadOnlySequence<byte> buffer)
    {
        foreach (ReadOnlyMemory<byte> segment in buffer)
        {
            if (MemoryMarshal.TryGetArray(segment, out ArraySegment<byte> array)) protocol.OfferInput(array.Array!, array.Offset, array.Count);
            else protocol.OfferInput(segment.ToArray());
        }
    }

    /// <summary>Writes whatever records the protocol has produced to the pipe, and flushes.</summary>
    internal static async ValueTask DrainAsync(TlsProtocol protocol, PipeWriter output, CancellationToken cancellationToken)
    {
        int pending = protocol.GetAvailableOutputBytes();
        if (pending == 0) return;
        byte[] records = ArrayPool<byte>.Shared.Rent(pending);
        try
        {
            int read = protocol.ReadOutput(records, 0, pending);
            await output.WriteAsync(records.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(records, clearArray: true);
        }
    }

    // BouncyCastle reads decrypted bytes into an array only: the caller's own when it has one, else the stream's read buffer.
    private int TakeInput(Memory<byte> buffer)
    {
        if (MemoryMarshal.TryGetArray<byte>(buffer, out ArraySegment<byte> array) && array.Array is { } bytes)
            return protocol.ReadInput(bytes, array.Offset, array.Count);
        byte[] input = _input ??= new byte[ReadSize];
        int read = protocol.ReadInput(input, 0, Math.Min(buffer.Length, input.Length));
        input.AsSpan(0, read).CopyTo(buffer.Span);
        return read;
    }
}
