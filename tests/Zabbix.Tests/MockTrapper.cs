// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Protocol;

namespace Community.Zabbix.Tests;

/// <summary>
/// An in-process trapper: accepts connections, answers each frame as upstream's trapper does (<c>processed: N; failed: N;
/// total: N; seconds spent: X</c>, <c>proxy.c</c>) for however many values it carried, and completes once it has taken at
/// least <c>expected</c> values in total — robust to how a send block batches. <see cref="Serve"/> goes on answering after
/// the count, until it is stopped. <see cref="Answer"/> is the same with a scripted reply, for the replies that are not a
/// success.
/// </summary>
internal static class MockTrapper
{
    public static (int Port, Task<int> Received) Collect(int expected, int processed = -1)
    {
        var port = new StrongBox<int>();
        Task<int> received = CollectAsync(port, expected, processed);                   // runs to its first await: bound
        return (port.Value, received);
    }

    /// <summary>
    /// A trapper that answers every frame until <paramref name="serving"/> is cancelled: <c>Received</c> completes once it has
    /// taken at least <paramref name="expected"/> values, while the trapper goes on answering, so a sender's later frames (a
    /// drain after the count, a loop that is not stopped) meet a reply, never a closed port. A peer that goes away
    /// mid-frame is dropped, not fatal.
    /// </summary>
    public static (int Port, Task<int> Received) Serve(int expected, CancellationToken serving)
    {
        var port = new StrongBox<int>();
        var counted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ServeAsync(port, expected, counted, serving);                               // runs to its first await: bound
        return (port.Value, counted.Task);
    }

    /// <summary>
    /// A trapper that answers <paramref name="frames"/> connections, one frame each, as upstream's trapper does, and returns
    /// each frame's request payload in order; the frame at <paramref name="refuseAt"/> (0-based) is answered with a refusal
    /// instead, as a server that took nothing answers.
    /// </summary>
    public static (int Port, Task<IReadOnlyList<byte[]>> Frames) Record(int frames, int refuseAt = -1)
    {
        var port = new StrongBox<int>();
        Task<IReadOnlyList<byte[]>> recorded = RecordAsync(port, frames, refuseAt);
        return (port.Value, recorded);
    }

    /// <summary>How many values a recorded frame carried.</summary>
    public static int Values(byte[] frame)
    {
        using JsonDocument document = JsonDocument.Parse(frame);
        return document.RootElement.GetProperty("data").GetArrayLength();
    }

    /// <summary>A trapper that answers the next frame with <paramref name="reply"/>, verbatim, then stops.</summary>
    public static (int Port, Task<string> Request) Answer(string reply)
    {
        var port = new StrongBox<int>();
        Task<string> request = AnswerAsync(port, reply);
        return (port.Value, request);
    }

    // Each serving task owns its listener: it binds before its first await (so the port is known on return) and
    // disposes the listener when it ends.
    private static async Task<int> CollectAsync(StrongBox<int> port, int expected, int processed)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port.Value = ((IPEndPoint)listener.LocalEndpoint).Port;
        int total = 0;
        while (total < expected)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            total += await AnswerFrameAsync(client, processed, CancellationToken.None);
        }
        return total;
    }

    private static async Task ServeAsync(StrongBox<int> port, int expected, TaskCompletionSource<int> counted, CancellationToken serving)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port.Value = ((IPEndPoint)listener.LocalEndpoint).Port;
        int total = 0;
        try
        {
            while (!serving.IsCancellationRequested)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(serving);
                try { total += await AnswerFrameAsync(client, -1, serving); }
                catch (IOException) when (!serving.IsCancellationRequested) { /* the peer went away mid-frame, or sent no frame: the next one */ }
                if (total >= expected) counted.TrySetResult(total);
            }
        }
        catch (OperationCanceledException) when (serving.IsCancellationRequested) { /* stopped */ }
        catch (SocketException failure) { counted.TrySetException(failure); }                  // the listener itself failed
        finally { counted.TrySetCanceled(serving); }                                    // stopped before the count
    }

    /// <summary>Answers one connection's frame as the trapper does and returns how many values it carried.</summary>
    private static async Task<int> AnswerFrameAsync(TcpClient client, int processed, CancellationToken cancellationToken)
    {
        await using NetworkStream stream = client.GetStream();
        ReadOnlyMemory<byte> request = await ZabbixHeader.ReadAsync(stream, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(request);
        int count = document.RootElement.GetProperty("data").GetArrayLength();
        int taken = processed < 0 ? count : Math.Min(processed, count);
        await ZabbixHeader.WriteAsync(stream, Encoding.UTF8.GetBytes(Success(taken, count - taken)), cancellationToken);
        return count;
    }

    private static async Task<IReadOnlyList<byte[]>> RecordAsync(StrongBox<int> port, int frames, int refuseAt)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port.Value = ((IPEndPoint)listener.LocalEndpoint).Port;
        List<byte[]> recorded = [];
        for (int i = 0; i < frames; i++)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            await using NetworkStream stream = client.GetStream();
            ReadOnlyMemory<byte> request = await ZabbixHeader.ReadAsync(stream);
            recorded.Add(request.ToArray());
            int count = Values(recorded[^1]);
            string reply = i == refuseAt ? """{"response":"failed","info":"cannot process request"}""" : Success(count, 0);
            await ZabbixHeader.WriteAsync(stream, Encoding.UTF8.GetBytes(reply));
        }
        return recorded;
    }

    private static async Task<string> AnswerAsync(StrongBox<int> port, string reply)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port.Value = ((IPEndPoint)listener.LocalEndpoint).Port;
        using TcpClient client = await listener.AcceptTcpClientAsync();
        await using NetworkStream stream = client.GetStream();
        ReadOnlyMemory<byte> frame = await ZabbixHeader.ReadAsync(stream);
        await ZabbixHeader.WriteAsync(stream, Encoding.UTF8.GetBytes(reply));
        return Encoding.UTF8.GetString(frame.Span);
    }

    /// <summary>Upstream's success reply for <paramref name="processed"/> taken and <paramref name="failed"/> refused.</summary>
    public static string Success(int processed, int failed)
        => string.Create(CultureInfo.InvariantCulture, $$"""{"response":"success","info":"processed: {{processed}}; failed: {{failed}}; total: {{processed + failed}}; seconds spent: 0.000100"}""");
}
