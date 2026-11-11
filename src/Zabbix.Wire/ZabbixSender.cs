// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Community.Zabbix;

/// <summary>
/// Sends values to a Zabbix server or proxy over the trapper protocol (<c>zabbix_sender</c>'s protocol), in frames of at
/// most <see cref="BatchSize"/> values and <see cref="MaxBatchBytes"/> bytes, and returns the trapper's counted answer. A
/// façade over a <see cref="ZabbixExchangeCallback"/> (one node, or an HA list) and the seam a store drains into
/// (<c>ZabbixWireClientExtensions.DrainAsync</c> in <c>Zabbix.Agent</c>). Direct async by design, never a block.
/// </summary>
/// <remarks>
/// <para>A sender built from nodes owns the connection it built, and with it the certificate and key certificate mode read
/// from its files: dispose the sender to release them. A sender over an exchange it was given owns nothing, and disposing it
/// does nothing.</para>
/// <para>A sender is thread-safe and meant to be shared: its settings are fixed when it is built, each call opens a
/// connection of its own, and the node list an HA pair fails over on is shared under a lock.</para>
/// </remarks>
internal sealed partial class ZabbixSender : IDisposable
{
    /// <summary>
    /// The most values one frame carries unless said otherwise: <c>zabbix_sender</c>'s <c>VALUES_MAX</c>
    /// (<c>src/zabbix_sender/send_buffer.h</c>), because a frame of many more values takes long enough to hit the
    /// timeout.
    /// </summary>
    public const int DefaultBatchSize = 250;

    /// <summary>
    /// The most bytes one frame's payload carries unless said otherwise: 1 GiB, the most a Zabbix server receives in one
    /// frame (<c>ZBX_MAX_RECV_DATA_SIZE</c>).
    /// </summary>
    public const long DefaultMaxBatchBytes = ZabbixHeader.MaxPayload;

    // {"request":"sender data","data":[]} as Utf8JsonWriter writes it: the bytes a frame carries besides its values.
    internal const int EnvelopeBytes = 35;

    // A first guess at one item's JSON, so the encoding buffer rarely grows: host, key, value, clock and ns.
    private const int TypicalItemBytes = 128;

    // The most the encoding buffer starts with; a larger call grows it.
    private const int MaxInitialBuffer = 1 << 20;

    private volatile bool _disposed;
    private readonly ZabbixExchangeCallback _exchange;
    private readonly ZabbixNodeLink? _link;
    private readonly ILogger _logger;

    /// <summary>A client over <paramref name="exchange"/>, which the caller keeps owning, that logs nothing.</summary>
    public ZabbixSender(ZabbixExchangeCallback exchange, string destination)
        : this(exchange, destination, logger: null)
    {
    }

    /// <summary>A client over <paramref name="exchange"/>, which the caller keeps owning.</summary>
    public ZabbixSender(ZabbixExchangeCallback exchange, string destination, ILogger? logger)
        : this(exchange, link: null, destination, logger)
    {
    }

    /// <summary>
    /// A client to the nodes, in order of preference (a server's HA pair, or a proxy and its fallback), that follows a
    /// proxy group's redirects as <c>zabbix_sender</c> does. The client owns the connection: dispose it.
    /// </summary>
    public ZabbixSender(IReadOnlyList<ZabbixServerNode> nodes)
        : this(nodes, connection: null)
    {
    }

    /// <summary>
    /// A client to the nodes, in order of preference (a server's HA pair, or a proxy and its fallback), that follows a
    /// proxy group's redirects as <c>zabbix_sender</c> does. The client owns the connection: dispose it.
    /// </summary>
    public ZabbixSender(IReadOnlyList<ZabbixServerNode> nodes, ZabbixConnectionOptions? connection)
        : this(nodes, connection, time: null, logger: null)
    {
    }

    /// <summary>
    /// A client to the nodes, in order of preference (a server's HA pair, or a proxy and its fallback), that follows a
    /// proxy group's redirects as <c>zabbix_sender</c> does. The client owns the connection: dispose it.
    /// </summary>
    public ZabbixSender(IReadOnlyList<ZabbixServerNode> nodes, ZabbixConnectionOptions? connection, TimeProvider? time, ILogger? logger)
        : this(nodes, connection, time, logger, telemetry: null)
    {
    }

    // The hosts pass the container's wire telemetry (the host's meters); a client built by hand reports on the wire's own.
    internal ZabbixSender(IReadOnlyList<ZabbixServerNode> nodes, ZabbixConnectionOptions? connection, TimeProvider? time, ILogger? logger, ZabbixWireTelemetry? telemetry)
        : this(new ZabbixNodeLink(nodes, connection, time, logger, telemetry), string.Join(", ", nodes), logger)
    {
    }

    /// <summary>
    /// A client to the trapper at <paramref name="host"/>, read as Zabbix reads one <c>ServerActive</c> element: on
    /// <see cref="ZabbixServerNode.DefaultPort"/> unless it names a port. The client owns the connection: dispose it.
    /// </summary>
    public ZabbixSender(string host)
        : this([ZabbixServerNode.Parse(host, ZabbixServerNode.DefaultPort, nameof(host))])
    {
    }

    /// <summary>
    /// A client to the trapper at <paramref name="host"/>:<paramref name="port"/>, in plaintext with the default deadline.
    /// The client owns the connection: dispose it. A deadline, TLS or a logger is the node list's constructor's.
    /// </summary>
    public ZabbixSender(string host, int port)
        : this([new ZabbixServerNode(host, port)])
    {
    }

    private ZabbixSender(ZabbixNodeLink link, string destination, ILogger? logger)
        : this(link.Exchange(followRedirects: true), link, destination, logger)
    {
    }

    private ZabbixSender(ZabbixExchangeCallback exchange, ZabbixNodeLink? link, string destination, ILogger? logger)
    {
        _link = link;
        try
        {
            ArgumentNullException.ThrowIfNull(exchange);
            ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        }
        catch
        {
            link?.Dispose();
            throw;
        }
        _exchange = exchange;
        Destination = destination;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The connection this client built and owns; <see langword="null"/> over a given exchange.</summary>
    internal ZabbixNodeLink? Link => _link;

    /// <summary>What the other end is called in logs.</summary>
    public string Destination { get; }

    /// <summary>
    /// The most values one frame carries; <see cref="DefaultBatchSize"/> (250, <c>zabbix_sender</c>'s) unless said
    /// otherwise. A call with more values sends several frames, one after another on the same node list.
    /// </summary>
    public int BatchSize { get; init; } = DefaultBatchSize;

    /// <summary>
    /// The most bytes one frame's JSON payload carries, before compression; <see cref="DefaultMaxBatchBytes"/> (1 GiB, the
    /// most a server receives) unless said otherwise. A frame closes early when the next value would pass it.
    /// </summary>
    public long MaxBatchBytes { get; init; } = DefaultMaxBatchBytes;

    /// <inheritdoc cref="SendAsync(IReadOnlyCollection{ZabbixValue}, CancellationToken)"/>
    public Task<ZabbixSenderResponse> SendAsync(IReadOnlyCollection<ZabbixValue> values) => SendAsync(values, CancellationToken.None);

    /// <inheritdoc cref="SendAsync(IReadOnlyCollection{ZabbixSenderItem}, CancellationToken)"/>
    public Task<ZabbixSenderResponse> SendAsync(IReadOnlyCollection<ZabbixSenderItem> items) => SendAsync(items, CancellationToken.None);

    /// <summary>Sends samples and returns the server's answer.</summary>
    public Task<ZabbixSenderResponse> SendAsync(IReadOnlyCollection<ZabbixValue> values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ZabbixSenderItem[] all = [.. values.Select(ZabbixSenderItem.From)];
        return SendCoreAsync(all, Encode(all, nameof(values)), cancellationToken);
    }

    /// <summary>
    /// Sends wire items and returns the server's answer. The items go in frames of at most <see cref="BatchSize"/> values
    /// and <see cref="MaxBatchBytes"/> bytes, in order, each sent when the one before was answered, as
    /// <c>zabbix_sender</c> sends its batches. A returned answer is a success: every frame was taken, and
    /// <see cref="ZabbixSenderResponse.Failed"/> counts the values the server refused inside them.
    /// </summary>
    /// <remarks>
    /// When the first frame fails, nothing was taken, and the failure is thrown as it is (the exceptions below). When a
    /// later frame fails, the frames before it were taken and sending stops there, as <c>zabbix_sender</c> stops at a
    /// failed batch: the failure is a <see cref="ZabbixSenderIncompleteException"/> that carries the answer for the frames
    /// taken and the items the server did not confirm, so a caller sends those again and nothing twice. A cancellation of
    /// <paramref name="cancellationToken"/> after the first frame was taken is a <see cref="ZabbixSenderCanceledException"/>
    /// carrying the same.
    /// </remarks>
    public Task<ZabbixSenderResponse> SendAsync(IReadOnlyCollection<ZabbixSenderItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ZabbixSenderItem[] all = [.. items];
        return SendCoreAsync(all, Encode(all, nameof(items)), cancellationToken);
    }

    // Every item serialized once into one buffer laid out as the frame for the whole call (envelope, items separated by
    // commas), with each item's end offset recorded, and checked to fit a frame alone before anything is sent: a usage
    // error is thrown at the call, under the caller's own parameter name, not when the task is awaited. A call that is one
    // frame sends that buffer as it is; a longer one cuts each frame at the offsets and copies it once into its envelope.
    private EncodedItems Encode(ZabbixSenderItem[] all, string parameter)
    {
        var buffer = new ArrayBufferWriter<byte>((int)Math.Clamp((long)all.Length * TypicalItemBytes, TypicalItemBytes, MaxInitialBuffer));
        buffer.Write(EnvelopeOpen);
        int[] ends = new int[all.Length];
        using (var json = new Utf8JsonWriter(buffer))
        {
            for (int i = 0; i < all.Length; i++)
            {
                if (i > 0) buffer.Write(","u8);
                JsonSerializer.Serialize(json, all[i], ZabbixSenderJson.Default.ZabbixSenderItem);
                json.Flush();
                json.Reset();                                                             // the next item is a value of its own, not a sibling
                ends[i] = buffer.WrittenCount;
            }
        }
        buffer.Write(EnvelopeClose);
        var encoded = new EncodedItems(buffer.WrittenMemory, ends);
        for (int i = 0; i < ends.Length; i++)
        {
            if (EnvelopeBytes + encoded.Length(i) > MaxBatchBytes)
                throw new ArgumentException($"Item {i} takes {EnvelopeBytes + encoded.Length(i)} bytes in a frame, more than the {MaxBatchBytes} bytes MaxBatchBytes allows; nothing was sent.", parameter);
        }
        return encoded;
    }

    // {"request":"sender data","data":[ ... ]}, the bytes Utf8JsonWriter writes for a SenderRequest around its items.
    private static ReadOnlySpan<byte> EnvelopeOpen => "{\"request\":\"sender data\",\"data\":["u8;

    private static ReadOnlySpan<byte> EnvelopeClose => "]}"u8;

    /// <summary>The whole call's frame, and where each item in it ends.</summary>
    private readonly record struct EncodedItems(ReadOnlyMemory<byte> Frame, int[] Ends)
    {
        public int Count => Ends.Length;

        public int Start(int index) => index == 0 ? EnvelopeOpen.Length : Ends[index - 1] + 1;

        public int Length(int index) => Ends[index] - Start(index);
    }

    private async Task<ZabbixSenderResponse> SendCoreAsync(ZabbixSenderItem[] all, EncodedItems encoded, CancellationToken cancellationToken)
    {
        var answers = new List<ZabbixSenderResponse>();
        int sent = 0;
        do
        {
            int count = FrameLength(encoded, sent);
            ReadOnlyMemory<byte> payload = Frame(encoded, sent, count);
            try
            {
                answers.Add(await SendFrameAsync(payload, count, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException cancelled) when (answers.Count > 0 && cancellationToken.IsCancellationRequested)
            {
                ZabbixSenderResponse taken = ZabbixSenderResponse.Sum(answers);
                WireLog.SenderStopped(_logger, Destination, answers.Count, taken.Total, all.Length - sent);
                throw new ZabbixSenderCanceledException(Destination, taken, [.. all[sent..].Select(static item => item.ToValue())], cancelled, cancellationToken);
            }
            catch (Exception failure) when (answers.Count > 0 && !(failure is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                ZabbixSenderResponse taken = ZabbixSenderResponse.Sum(answers);
                WireLog.SenderStopped(_logger, Destination, answers.Count, taken.Total, all.Length - sent);
                throw new ZabbixSenderIncompleteException(Destination, taken, [.. all[sent..].Select(static item => item.ToValue())], failure);
            }
            sent += count;
        }
        while (sent < all.Length);
        return ZabbixSenderResponse.Sum(answers);
    }

    /// <summary>Sends <paramref name="values"/> as one frame, whatever <see cref="BatchSize"/> says: a send loop's batch is its frame.</summary>
    internal Task<ZabbixSenderResponse> SendFrameAsync(IReadOnlyCollection<ZabbixValue> values, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ZabbixSenderItem[] items = [.. values.Select(ZabbixSenderItem.From)];
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new SenderRequest("sender data", items), ZabbixSenderJson.Default.SenderRequest);
        return SendFrameAsync(payload, items.Length, cancellationToken);
    }

    /// <summary>Releases the connection the client built (its certificate trust); a client over a given exchange has none. A disposed sender sends nothing.</summary>
    public void Dispose()
    {
        _disposed = true;
        _link?.Dispose();
    }

    // How many items from `start` go in one frame: up to BatchSize, and while the frame stays within MaxBatchBytes. Always
    // at least one when any remain (each item was checked to fit alone); none for an empty call, which is one empty frame.
    private int FrameLength(EncodedItems encoded, int start)
    {
        long bytes = EnvelopeBytes;
        int count = 0;
        while (start + count < encoded.Count && count < BatchSize)
        {
            long next = bytes + encoded.Length(start + count) + (count > 0 ? 1 : 0);
            if (count > 0 && next > MaxBatchBytes) break;
            bytes = next;
            count++;
        }
        return count;
    }

    // The frame for `count` items from `start`: the whole encoded call when that is all of it, otherwise sized exactly from
    // the offsets, with the items' bytes (and the commas between them) copied in once, as they were encoded.
    private static ReadOnlyMemory<byte> Frame(EncodedItems encoded, int start, int count)
    {
        if (start == 0 && count == encoded.Count) return encoded.Frame;
        ReadOnlySpan<byte> items = encoded.Frame.Span[encoded.Start(start)..encoded.Ends[start + count - 1]];
        byte[] frame = new byte[EnvelopeBytes + items.Length];
        EnvelopeOpen.CopyTo(frame);
        items.CopyTo(frame.AsSpan(EnvelopeOpen.Length));
        EnvelopeClose.CopyTo(frame.AsSpan(EnvelopeOpen.Length + items.Length));
        return frame;
    }

    private async Task<ZabbixSenderResponse> SendFrameAsync(ReadOnlyMemory<byte> payload, int count, CancellationToken cancellationToken)
    {
        WireLog.SenderSending(_logger, count, Destination);
        try
        {
            ReadOnlyMemory<byte> reply = await _exchange(payload, cancellationToken).ConfigureAwait(false);
            ZabbixSenderResponse response = Parse(reply.Span);
            WireLog.SenderSent(_logger, response.Total, Destination, response.Processed, response.Failed);
            if (response.HasFailures)
                WireLog.SenderReportedFailures(_logger, response.Failed, response.Total, Destination);
            return response;
        }
        catch (ZabbixRedirectException redirected)
        {
            WireLog.SenderRedirected(_logger, Destination, redirected.Redirect?.ToString() ?? redirected.Message);
            throw;
        }
        catch (ZabbixTrapperRefusedException refused)
        {
            WireLog.SenderRefused(_logger, Destination, refused.Response ?? "none", refused.Info);
            throw;
        }
        catch (Exception failure) when (ZabbixFrames.IsExchangeFailure(failure))
        {
            WireLog.SenderFailed(_logger, Destination, failure);
            throw;
        }
    }

    [GeneratedRegex(@"^processed: (\d+); failed: (\d+); total: (\d+); seconds spent: ([0-9.]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Counters();

    // zabbix_sender's check_response: `response` must be "success"; a "failed" with a redirect block is a redirect the
    // exchange did not follow (one that follows never hands one back). The info line is upstream's zbx_dsprintf format
    // (proxy.c); an info this cannot count is refused rather than read as zero.
    private static ZabbixSenderResponse Parse(ReadOnlySpan<byte> payload)
    {
        if (ZabbixRedirect.TryParse(payload, out ZabbixRedirect? unfollowed)) throw ZabbixRedirectException.Unfollowed(unfollowed);

        RawSenderResponse raw;
        try { raw = JsonSerializer.Deserialize(payload, ZabbixSenderJson.Default.RawSenderResponse) ?? throw new InvalidDataException("The trapper answered with no object."); }
        catch (JsonException invalid) { throw new InvalidDataException("The trapper's reply is not JSON: " + invalid.Message, invalid); }

        if (!string.Equals(raw.Response, "success", StringComparison.Ordinal)) throw new ZabbixTrapperRefusedException(raw.Response, raw.Info);

        // The pattern admits digits past int and a decimal of dots ("1.2.3", "."), so each counter is parsed with TryParse:
        // a count that does not parse is a reply this cannot count, the same refusal as one that does not match.
        Match match = Counters().Match(raw.Info ?? string.Empty);
        if (!match.Success
            || !int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int processed)
            || !int.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int failed)
            || !int.TryParse(match.Groups[3].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int total)
            || !double.TryParse(match.Groups[4].ValueSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds))
            throw new ZabbixTrapperRefusedException(raw.Response, raw.Info);
        return new ZabbixSenderResponse(processed, failed, total, TimeSpan.FromSeconds(seconds), match.Value);                // the whole info line: the pattern is anchored
    }
}
