// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using Org.BouncyCastle.Tls;

namespace Community.Zabbix.Protocol;

/// <summary>
/// A <see cref="Stream"/> over a BouncyCastle client in non-blocking mode and the connection's own stream: a read offers
/// the connection's bytes to the protocol and returns what it decrypted, a write hands application data to the protocol
/// and writes its records out. Every wait is an asynchronous read or write on the connection, so no pool thread blocks
/// in a handshake or a read and the caller's token cancels any of them; the listener's PSK server is the same shape over
/// a pipe. The stream owns the connection and closes it, after a <c>close_notify</c>, when disposed.
/// </summary>
internal sealed class ZabbixPskClientStream : Stream
{
    // A TLS record is at most 2^14 bytes of plaintext plus its expansion (RFC 8446 5.2); one read takes one record whole.
    private const int ReadSize = (1 << 14) + 2048;

    private readonly TlsClientProtocol _protocol;
    private readonly Stream _connection;
    private readonly byte[] _input = new byte[ReadSize];

    private ZabbixPskClientStream(TlsClientProtocol protocol, Stream connection)
    {
        _protocol = protocol;
        _connection = connection;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <summary>Runs the client's handshake over <paramref name="connection"/> and answers the stream that speaks over it.</summary>
    /// <param name="connection">The connected transport; owned by the returned stream.</param>
    /// <param name="client">The BouncyCastle client: the suites, versions and the key.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <returns>The protected stream.</returns>
    /// <exception cref="TlsException">Either end refused the handshake; the alert this end raised has been sent.</exception>
    /// <exception cref="EndOfStreamException">The peer closed before the handshake completed.</exception>
    public static async ValueTask<ZabbixPskClientStream> ConnectAsync(Stream connection, TlsClient client, CancellationToken cancellationToken)
    {
        var protocol = new TlsClientProtocol();                                              // non-blocking: no stream, we pump
        var stream = new ZabbixPskClientStream(protocol, connection);
        try
        {
            protocol.Connect(client);
            while (true)
            {
                await stream.DrainAsync(cancellationToken).ConfigureAwait(false);
                if (!protocol.IsHandshaking) return stream;
                int read = await connection.ReadAsync(stream._input, cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The peer closed the connection during the pre-shared-key handshake.");
                protocol.OfferInput(stream._input, 0, read);
            }
        }
        catch (TlsException)
        {
            // BouncyCastle queued the alert it raised (decrypt_error, …): send it, as OpenSSL does, so the peer's log says why.
            try { await stream.DrainAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception unsent) when (unsent is IOException or ObjectDisposedException or OperationCanceledException) { /* the peer is gone */ }
            throw;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // A zero-length read waits for decrypted bytes (or the end) and takes nothing, as SslStream's does.
        while (true)
        {
            if (_protocol.GetAvailableInputBytes() > 0) return buffer.IsEmpty ? 0 : TakeInput(buffer);
            if (_protocol.IsClosed) return 0;
            int read = await _connection.ReadAsync(_input, cancellationToken).ConfigureAwait(false);
            if (read == 0) return 0;                                                          // the peer closed without close_notify
            _protocol.OfferInput(_input, 0, read);
            await DrainAsync(cancellationToken).ConfigureAwait(false);                        // alerts the offer produced
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _protocol.WriteApplicationData(buffer.Span);
        await DrainAsync(cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) => _connection.FlushAsync(cancellationToken);

    /// <summary>Does nothing: the stream buffers nothing, every write hands its records to the connection before it returns.</summary>
    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("The pre-shared-key stream is asynchronous only.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The pre-shared-key stream is asynchronous only.");

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (!_protocol.IsClosed)
        {
            _protocol.Close();                                                                // close_notify, as upstream's SSL_shutdown
            try { await DrainAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception failure) when (failure is IOException or ObjectDisposedException) { /* the peer is gone; nothing to tell it */ }
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the connection without sending <c>close_notify</c>: sending it is a write, and the stream does no synchronous
    /// I/O. The peer sees the connection close, which upstream's TLS reads as the end of the session too.
    /// <see cref="DisposeAsync"/> sends <c>close_notify</c> first.
    /// </summary>
    /// <param name="disposing">Whether the call is <see cref="Stream.Dispose()"/>'s.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!_protocol.IsClosed) _protocol.Close();
            _connection.Dispose();
        }
        base.Dispose(disposing);
    }

    // BouncyCastle reads decrypted bytes into an array only: the caller's own when it has one, else the read buffer, which
    // is free while the protocol holds decrypted input (a record is offered whole before anything is taken from it).
    private int TakeInput(Memory<byte> buffer)
    {
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray<byte>(buffer, out ArraySegment<byte> array) && array.Array is { } bytes)
            return _protocol.ReadInput(bytes, array.Offset, array.Count);
        int read = _protocol.ReadInput(_input, 0, Math.Min(buffer.Length, _input.Length));
        _input.AsSpan(0, read).CopyTo(buffer.Span);
        return read;
    }

    /// <summary>Writes whatever records the protocol has produced to the connection.</summary>
    private async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        int pending = _protocol.GetAvailableOutputBytes();
        if (pending == 0) return;
        byte[] records = ArrayPool<byte>.Shared.Rent(pending);
        try
        {
            int read = _protocol.ReadOutput(records, 0, pending);
            await _connection.WriteAsync(records.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(records, clearArray: true);
        }
    }
}
