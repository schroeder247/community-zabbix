// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Community.Zabbix.Tests;

/// <summary>What the relay does with one request.</summary>
internal enum LiveRelayAction
{
    /// <summary>Forward it and relay the reply.</summary>
    Forward,

    /// <summary>Forward it, read the server's reply, and cut the client off: the server processed what the client never hears about.</summary>
    CutAfter,

    /// <summary>Cut the client off without forwarding: the server never saw it (an unreachable server, as the client sees it).</summary>
    CutBefore,
}

/// <summary>
/// A frame-aware TCP relay between a Zabbix client and a real server, for the failures a live server never produces on
/// its own: each connection's request is read whole and <see cref="Decide"/> chooses, per request, to forward it, to
/// forward it and cut the client off <b>after</b> the server processed it (the "killed mid-upload" case, where the client
/// must re-send with the same ids and the server's <c>last_id</c> must drop the duplicate), or to cut the client off
/// before the server sees it.
/// </summary>
internal sealed class LiveRelay : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ZabbixServerNode _target;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private int _dropped;

    public LiveRelay(ZabbixServerNode target, Func<string, bool> dropReply)
        : this(target, payload => dropReply(payload) ? LiveRelayAction.CutAfter : LiveRelayAction.Forward)
    {
    }

    public LiveRelay(ZabbixServerNode target, Func<string, LiveRelayAction> decide)
    {
        _target = target;
        Decide = decide;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    /// <summary>Given the request's payload (uncompressed JSON), what to do with it; may change its mind between requests.</summary>
    public Func<string, LiveRelayAction> Decide { get; set; }

    /// <summary>Where the client connects.</summary>
    public ZabbixServerNode Node => new("127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port);

    /// <summary>How many requests were cut (before or after the server).</summary>
    public int Dropped => Volatile.Read(ref _dropped);

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            _ = RelayAsync(client);
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        using (client)
        using (var server = new TcpClient())
        {
            try
            {
                NetworkStream fromClient = client.GetStream();
                (byte[] request, string payload) = await ReadFrameAsync(fromClient, _stop.Token);
                LiveRelayAction action = Decide(payload);
                if (action == LiveRelayAction.CutBefore)
                {
                    Interlocked.Increment(ref _dropped);
                    client.Client.LingerState = new LingerOption(true, 0);                    // a reset, as a killed process leaves it
                    return;
                }
                await server.ConnectAsync(_target.Host, _target.Port, _stop.Token);
                NetworkStream toServer = server.GetStream();
                await toServer.WriteAsync(request, _stop.Token);
                (byte[] reply, _) = await ReadFrameAsync(toServer, _stop.Token);
                if (action == LiveRelayAction.CutAfter)
                {
                    Interlocked.Increment(ref _dropped);
                    client.Client.LingerState = new LingerOption(true, 0);
                    return;
                }
                await fromClient.WriteAsync(reply, _stop.Token);
            }
            catch (Exception failure) when (failure is IOException or SocketException or OperationCanceledException)
            {
            }
        }
    }

    // ZBXD, flags, then 4+4 (or, with 0x04, 8+8) byte little-endian lengths: the data length and the uncompressed one.
    private static async Task<(byte[] Frame, string Payload)> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] head = new byte[5];
        await stream.ReadExactlyAsync(head, cancellationToken);
        bool large = (head[4] & 0x04) != 0;
        byte[] lengths = new byte[large ? 16 : 8];
        await stream.ReadExactlyAsync(lengths, cancellationToken);
        long length = large ? BinaryPrimitives.ReadInt64LittleEndian(lengths) : BinaryPrimitives.ReadInt32LittleEndian(lengths);
        byte[] data = new byte[length];
        await stream.ReadExactlyAsync(data, cancellationToken);
        bool compressed = (head[4] & 0x02) != 0;
        return ([.. head, .. lengths, .. data], compressed ? string.Empty : Encoding.UTF8.GetString(data));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Dispose();
        await _accepting;
        _stop.Dispose();
    }
}
