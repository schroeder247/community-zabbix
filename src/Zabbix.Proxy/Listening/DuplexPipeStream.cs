// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.IO.Pipelines;

namespace Community.Zabbix.Listening;

/// <summary>
/// A <see cref="Stream"/> over an <see cref="IDuplexPipe"/>, for the one hop that needs one: <see cref="System.Net.Security.SslStream"/>
/// and BouncyCastle speak streams, the connection speaks pipes. Reads are the input pipe's stream, writes the output's
/// (<c>PipeReader.AsStream</c> / <c>PipeWriter.AsStream</c>); nothing is copied. It is owned because the public
/// surface has no duplex pipe stream (Kestrel's own is internal).
/// </summary>
internal sealed class DuplexPipeStream(IDuplexPipe pipe) : Stream
{
    private readonly Stream _input = pipe.Input.AsStream(leaveOpen: true);
    private readonly Stream _output = pipe.Output.AsStream(leaveOpen: true);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _output.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _output.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _input.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _input.ReadAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _output.Write(buffer);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _output.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _output.WriteAsync(buffer, offset, count, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _input.Dispose();
            _output.Dispose();
        }
        base.Dispose(disposing);
    }
}
