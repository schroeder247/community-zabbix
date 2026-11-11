// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.IO.Pipelines;

namespace Community.Zabbix;

/// <summary>
/// Reads the reply's body inside the deadline. The innermost handler of a client's pipeline, below the standard
/// resilience handler, it reads the whole body under the token that carries the per-attempt and total timeouts, so a
/// server that stalls mid-body fails the attempt as a <c>TimeoutRejectedException</c> and the read is retried, instead
/// of holding the caller for ever once the headers have arrived.
/// </summary>
/// <remarks>
/// The body is bounded by <see cref="ZabbixClientOptions.MaxResponseContentBytes"/>: a larger one fails as
/// <see cref="InvalidDataException"/>, which the standard retry does not handle, so an oversized read is not sent again.
/// The body is copied once into a <see cref="Pipe"/>'s pooled segments and handed on as a
/// <see cref="ZabbixBufferedContent"/> the transport reads in place. <c>HttpClient.Timeout</c> and
/// <c>HttpCompletionOption.ResponseContentRead</c> act outside the resilience handler, and
/// <see cref="HttpContent.LoadIntoBufferAsync(long, CancellationToken)"/> buffers into a growing array the transport
/// would copy a second time.
/// </remarks>
internal sealed class ZabbixResponseBufferingHandler(long maxBytes) : DelegatingHandler
{
    // Never paused: the body is wanted whole, and the reader below only watches it grow past the bound.
    private static readonly PipeOptions s_unpaused = new(pauseWriterThreshold: 0, minimumSegmentSize: 16 * 1024, useSynchronizationContext: false);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed", Justification = "The copy is awaited on every path, the success and the catch, before the token source is disposed.")]
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var pipe = new Pipe(s_unpaused);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task copying = Task.CompletedTask;
        try
        {
            long bound = Math.Min(maxBytes, int.MaxValue);
            if (response.Content.Headers.ContentLength is long declared && declared > bound) throw TooLarge(request, bound);

            // CopyToAsync hands the token to the content itself (a connection's body, or any HttpContent), where
            // ReadAsStreamAsync would buffer a content that is not a connection's first, without it; its failures are
            // the platform's HttpRequestException ("Error while copying content to a stream"), which the retry knows.
            HttpContent received = response.Content;
            copying = CopyAsync(received, pipe.Writer, stop.Token);
            ReadResult read;
            while (true)
            {
                // Nothing consumed, everything examined: the next read waits for more, and a body past the bound is refused
                // as soon as it passes it, never read to its end.
                read = await pipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (read.Buffer.Length > bound) throw TooLarge(request, bound);
                if (read.IsCompleted) break;
                pipe.Reader.AdvanceTo(read.Buffer.Start, read.Buffer.End);
            }
            await copying.ConfigureAwait(false);
            response.Content = new ZabbixBufferedContent(pipe.Reader, read.Buffer, received.Headers);
            received.Dispose();
            return response;
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await copying.ConfigureAwait(false);
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            response.Dispose();
            throw;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Not swallowed: the failure completes the pipe, and the reader's ReadAsync throws it.")]
    private static async Task CopyAsync(HttpContent content, PipeWriter writer, CancellationToken cancellationToken)
    {
        try
        {
            Stream target = writer.AsStream(leaveOpen: true);
            await using (target.ConfigureAwait(false)) await content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            await writer.CompleteAsync(failure).ConfigureAwait(false);
        }
    }

    private static InvalidDataException TooLarge(HttpRequestMessage request, long bound)
    {
        string method = request.Options.TryGetValue(ZabbixHttp.Method, out ZabbixApiMethod name) ? name.Name : "the request";
        return new InvalidDataException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"The reply to {method} is larger than MaxResponseContentBytes ({bound} bytes)."));
    }
}
