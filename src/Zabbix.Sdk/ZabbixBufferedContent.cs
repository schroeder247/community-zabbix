// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;

namespace Community.Zabbix;

/// <summary>
/// A reply's body, read whole by <see cref="ZabbixResponseBufferingHandler"/>: <see cref="Body"/> is the bytes in the pooled
/// segments they arrived in, valid until the response is disposed (which completes the pipe's reader and returns them). Readable again as a stream, for
/// any reader that does not know it. Owned because <see cref="HttpContent.LoadIntoBufferAsync()"/> copies the body into one
/// contiguous array, where these segments are read in place.
/// </summary>
internal sealed class ZabbixBufferedContent : HttpContent
{
    private readonly PipeReader _reader;

    public ZabbixBufferedContent(PipeReader reader, ReadOnlySequence<byte> body, HttpContentHeaders received)
    {
        _reader = reader;
        Body = body;
        foreach (KeyValuePair<string, IEnumerable<string>> header in received) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    /// <summary>The whole body.</summary>
    public ReadOnlySequence<byte> Body { get; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        foreach (ReadOnlyMemory<byte> segment in Body) await stream.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = Body.Length;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _reader.Complete();
        base.Dispose(disposing);
    }
}
