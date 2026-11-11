// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Protocol;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// A real TCP peer speaking the Zabbix frame protocol (<c>ZBXD</c>, <see cref="ZabbixHeader"/>) on a loopback port — a
/// server trapper, an agent, a proxy's server: each request frame is recorded and answered by the script for its
/// <c>request</c> field (or by <see cref="Otherwise"/>), one frame per connection as upstream does. Started with a
/// pre-shared key (<see cref="StartPsk"/>), it accepts TLS with that key only, as a server whose host accepts only
/// <c>psk</c>, and a plain frame or another key fails the connection.
/// </summary>
internal sealed class FakeTrapper : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Func<JsonElement, string>> _script = new(StringComparer.Ordinal);
    private readonly Task _accepting;
    private readonly (byte[] Identity, byte[] Key)? _psk;

    private FakeTrapper((byte[] Identity, byte[] Key)? psk = null)
    {
        _psk = psk;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptAsync(_stop.Token);
    }

    public int Port { get; }

    /// <summary>This peer as the SDK names a node.</summary>
    public ZabbixServerNode Node => new("127.0.0.1", Port);

    /// <summary>Every request frame, in arrival order: its <c>request</c> field (or the raw text) and its JSON.</summary>
    public ConcurrentQueue<(string Request, string Text)> Frames { get; } = new();

    /// <summary>The answer to a frame no script matches; a sender's success by default.</summary>
    public Func<string, string> Otherwise { get; set; } = _ => Success(1, 0);

    /// <summary>Completed with the text of each frame as it arrives.</summary>
    public TaskCompletionSource<string> Arrived { get; private set; } = NewSignal();

    public static FakeTrapper Start() => new();

    /// <summary>A trapper that takes only TLS with the key <paramref name="hexKey"/> under <paramref name="identity"/>.</summary>
    public static FakeTrapper StartPsk(string identity, string hexKey) => new((Encoding.UTF8.GetBytes(identity), Convert.FromHexString(hexKey)));

    /// <summary>The number of connections whose handshake or frame failed (a refused key, a plain frame to a PSK trapper).</summary>
    public int Refused => Volatile.Read(ref _refused);

    private int _refused;

    public FakeTrapper On(string request, Func<JsonElement, string> answer)
    {
        _script[request] = answer;
        return this;
    }

    public FakeTrapper On(string request, string answer) => On(request, _ => answer);

    public TaskCompletionSource<string> ExpectArrival() => Arrived = NewSignal();

    /// <summary>The JSON of every frame whose <c>request</c> is <paramref name="request"/>.</summary>
    public IEnumerable<JsonElement> FramesOf(string request)
        => Frames.Where(frame => frame.Request == request).Select(frame => JsonDocument.Parse(frame.Text).RootElement.Clone());

    /// <summary>Upstream's sender/trapper success reply (<c>processed: N; failed: N; total: N; seconds spent: X</c>).</summary>
    public static string Success(int processed, int failed)
        => string.Create(CultureInfo.InvariantCulture, $$"""{"response":"success","info":"processed: {{processed}}; failed: {{failed}}; total: {{processed + failed}}; seconds spent: 0.000100"}""");

    private async Task AcceptAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(stopping);
            }
            catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = AnswerAsync(client, stopping);
        }
    }

    private async Task AnswerAsync(TcpClient client, CancellationToken stopping)
    {
        using (client)
        {
            try
            {
                await using NetworkStream network = client.GetStream();
                Stream stream = network;
                if (_psk is { } psk)
                {
                    // BouncyCastle's blocking server handshake, on a pool thread of its own.
                    var server = new TlsServerProtocol(network);
                    await Task.Run(() => server.Accept(new OneKeyServer(psk.Identity, psk.Key)), stopping);
                    stream = server.Stream;
                }
                ReadOnlyMemory<byte> frame = await ZabbixHeader.ReadAsync(stream, stopping);
                string text = Encoding.UTF8.GetString(frame.Span);
                string request = RequestOf(text);
                Frames.Enqueue((request, text));
                Arrived.TrySetResult(text);
                string reply = _script.TryGetValue(request, out Func<JsonElement, string>? answer)
                    ? answer(JsonDocument.Parse(text).RootElement.Clone())
                    : Otherwise(text);
                await ZabbixHeader.WriteAsync(stream, Encoding.UTF8.GetBytes(reply), stopping);
            }
            catch (Exception failure) when (failure is TlsException)
            {
                Interlocked.Increment(ref _refused);                     // a key or a hello the PSK server refused
            }
            catch (Exception failure) when (failure is IOException or OperationCanceledException or SocketException or InvalidDataException)
            {
                // The client went away or the fake is stopping: nothing to answer.
                Interlocked.Increment(ref _refused);
            }
        }
    }

    private static string RequestOf(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("request", out JsonElement request)
                ? request.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return text;                                          // a passive check's plain key (pre-JSON agents)
        }
    }

    /// <summary>A plain BouncyCastle PSK server: TLS 1.2, one identity, no hint.</summary>
    private sealed class OneKeyServer(byte[] identity, byte[] key) : PskTlsServer(new BcTlsCrypto(new SecureRandom()), new OneIdentity(identity, key))
    {
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv12];

        private sealed class OneIdentity(byte[] identity, byte[] key) : TlsPskIdentityManager
        {
            public byte[]? GetHint() => null;

            // A copy: the handshake clears the array it is given, and the next connection needs the key again.
            public byte[]? GetPsk(byte[] offered) => offered.AsSpan().SequenceEqual(identity) ? (byte[])key.Clone() : null;
        }
    }

    private static TaskCompletionSource<string> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Dispose();
        await _accepting;
        _stop.Dispose();
    }
}
