// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.IO.Pipelines;
using Community.Zabbix.Protocol;

namespace Community.Zabbix.Listening;

/// <summary>
/// Provides one connection's frames to the conversation that owns it. The listener has read the first frame
/// (<see cref="Request"/>); <see cref="ReadAsync"/> and <see cref="WriteAsync"/> carry the rest, framed and, when the
/// listener's options say so, compressed, over the connection's pipes (TLS already unwrapped). Named as ASP.NET Core
/// names the connection a handler owns (<c>ConnectionContext</c>, <c>HttpContext</c>).
/// </summary>
public sealed class ZabbixConversationContext
{
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly long _maxPayload;
    private readonly bool _compress;

    /// <summary>A conversation over a connection's pipes.</summary>
    /// <param name="request">The first frame, already read.</param>
    /// <param name="reader">The connection's input.</param>
    /// <param name="writer">The connection's output.</param>
    /// <param name="maxPayload">The largest frame accepted.</param>
    /// <param name="compress">Whether frames written are compressed.</param>
    internal ZabbixConversationContext(ZabbixRequest request, PipeReader reader, PipeWriter writer, long maxPayload, bool compress)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        Request = request;
        _reader = reader;
        _writer = writer;
        _maxPayload = maxPayload;
        _compress = compress;
    }

    /// <summary>The first frame, and who sent it.</summary>
    public ZabbixRequest Request { get; }

    /// <summary>Bytes of payload written so far.</summary>
    public long BytesWritten { get; private set; }

    /// <summary>
    /// Whether a write has begun: from then on the connection's output may hold bytes that only the peer's reads drain,
    /// so the listener aborts rather than closes it at the deadline.
    /// </summary>
    internal bool Writing { get => Volatile.Read(ref _writing); private set => Volatile.Write(ref _writing, value); }

    private bool _writing;

    /// <summary>Reads the peer's next frame, under the listener's payload cap.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The payload, inflated.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a Zabbix frame, or the peer closed the connection first.</exception>
    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
        => ZabbixHeader.ReadAsync(_reader, _maxPayload, allowLarge: false, cancellationToken);

    /// <summary>
    /// Reads the peer's next frame as a large one, under upstream's large cap (16 GiB) rather than the listener's:
    /// <c>zbx_tcp_recv_large</c>, which upstream uses only where a passive proxy receives its configuration
    /// (<c>proxyconfigwrite.c</c>).
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The payload, inflated.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a Zabbix frame, or the peer closed the connection first.</exception>
    public ValueTask<ReadOnlyMemory<byte>> ReadLargeAsync(CancellationToken cancellationToken = default)
        => ZabbixHeader.ReadAsync(_reader, maxPayload: null, allowLarge: true, cancellationToken);

    /// <summary>Writes one frame to the peer.</summary>
    /// <param name="payload">The payload.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>When it is flushed.</returns>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        Writing = true;
        await ZabbixHeader.WriteAsync(_writer, payload, _compress, cancellationToken).ConfigureAwait(false);
        BytesWritten += payload.Length;
    }

    /// <summary>A request/reply handler as a conversation: answer the first frame, and write nothing when the reply is empty.</summary>
    /// <param name="handler">The handler.</param>
    /// <returns>The conversation.</returns>
    public static ZabbixConversationCallback Reply(ZabbixRequestCallback handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return async (conversation, cancellationToken) =>
        {
            ReadOnlyMemory<byte> reply = await handler(conversation.Request, cancellationToken).ConfigureAwait(false);
            if (reply.Length > 0) await conversation.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
        };
    }
}
