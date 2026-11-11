// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.IO.Pipelines;
using Community.Zabbix.Listening;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Community.Zabbix.Tests;

/// <summary>
/// The two pre-shared-key streams over an in-memory connection: the listener's (<see cref="ZabbixPskStream"/>, accepted by
/// <see cref="ZabbixTlsAcceptor"/> over pipes) and the client's (<see cref="ZabbixPskClientStream"/>). A read decrypts
/// into the caller's array without a buffer of its own per call, and a zero-length read waits for data and takes
/// nothing, as <see cref="System.Net.Security.SslStream"/> does, so 0 means the end of the stream only for a real buffer.
/// </summary>
public sealed class PskStreamTests
{
    private const string Identity = "edge-01";
    private const string Key = "1f87b595725ac58dd977beef14b97461a7c1045b9a1c963065002c5473194952";

    [Fact]
    public async Task A_zero_length_read_on_the_listener_s_stream_waits_for_data_and_takes_none_of_it()
    {
        await using Connected connected = await ConnectAsync();

        ValueTask<int> waiting = connected.Server.ReadAsync(Memory<byte>.Empty, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        await connected.Client.WriteAsync("hello"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(0, await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        byte[] buffer = new byte[16];
        int read = await connected.Server.ReadAsync(buffer, TestContext.Current.CancellationToken);
        Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task A_zero_length_read_on_the_client_s_stream_waits_for_data_and_takes_none_of_it()
    {
        await using Connected connected = await ConnectAsync();

        ValueTask<int> waiting = connected.Client.ReadAsync(Memory<byte>.Empty, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        await connected.Server.WriteAsync("hello"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(0, await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        byte[] buffer = new byte[16];
        int read = await connected.Client.ReadAsync(buffer, TestContext.Current.CancellationToken);
        Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task Reading_decrypted_bytes_on_the_listener_s_stream_allocates_no_buffer_per_read()
    {
        await using Connected connected = await ConnectAsync();
        await connected.Client.WriteAsync(new byte[12 * 1024], TestContext.Current.CancellationToken);   // one record
        byte[] buffer = new byte[4 * 1024];
        Assert.Equal(1, await connected.Server.ReadAsync(buffer.AsMemory(0, 1), TestContext.Current.CancellationToken));   // the record is decrypted

        long before = GC.GetAllocatedBytesForCurrentThread();
        ValueTask<int> reading = connected.Server.ReadAsync(buffer, TestContext.Current.CancellationToken);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(reading.IsCompletedSuccessfully);                                       // decrypted bytes are read without a wait
        Assert.Equal(buffer.Length, await reading);
        Assert.True(allocated < 512, $"a read of decrypted bytes allocated {allocated} bytes");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The acceptor and the client's transport are owned by the returned Connected (the client's PSK stream owns its transport), which the test disposes.")]
    private static async Task<Connected> ConnectAsync()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var listening = new ZabbixListenerOptions { TlsAccept = ZabbixTlsAcceptModes.Psk };
        listening.PreSharedKeys[Identity] = Key;
        var acceptor = new ZabbixTlsAcceptor(listening, NullLogger.Instance, TimeProvider.System);
        ValueTask<ZabbixAcceptedConnection> accepting = acceptor.AcceptAsync(new DuplexPipe(toServer.Reader, toClient.Writer), "client", TestContext.Current.CancellationToken);
        var words = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = Identity, Psk = Key };
        Stream client = await ZabbixTls.WrapClientAsync(new DuplexPipeStream(new DuplexPipe(toClient.Reader, toServer.Writer)), "server", words, null, NullLogger.Instance, TestContext.Current.CancellationToken);
        ZabbixAcceptedConnection accepted = await accepting;
        return new Connected(acceptor, client, accepted.Owned!);
    }

    private sealed record Connected(ZabbixTlsAcceptor Acceptor, Stream Client, Stream Server) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            Acceptor.Dispose();
        }
    }
}
