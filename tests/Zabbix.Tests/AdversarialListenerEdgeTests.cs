// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Community.Zabbix.Listening;
using Community.Zabbix.Protocol;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>
/// Adversarial tests of the listeners' edges that the ordinary listener tests do not reach: a frame twelve times Kestrel's default
/// read buffer (1 MiB) on the composed socket transport, read whole although the header reader consumes nothing until the
/// frame is complete; the pre-shared key's decoded bytes after a handshake; and the file descriptors a listener holds
/// over its lifetime.
/// </summary>
[Collection("Adversarial wire stress")]
public sealed class AdversarialListenerEdgeTests
{
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(30);

    private static IConnectionListenerFactory ComposedTransport(ServiceCollection services)
    {
        services.AddZabbixSocketTransport();
        return services.BuildServiceProvider().GetRequiredKeyedService<IConnectionListenerFactory>(ZabbixListener.TransportKey);
    }

    [Fact]
    public async Task An_unencrypted_twelve_mebibyte_incompressible_frame_is_read_whole_under_the_transport_s_default_read_buffer()
    {
        var services = new ServiceCollection();
        IConnectionListenerFactory transport = ComposedTransport(services);
        var options = new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0, Timeout = TimeSpan.FromMinutes(2), MaxPayloadBytes = 16 * 1024 * 1024 };
        options.AllowedPeers.Add("127.0.0.1");
        var listener = new ZabbixListener(options, (request, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.ASCII.GetBytes(request.Payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))), transport);
        await listener.StartingAsync(TestContext.Current.CancellationToken);
        await listener.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            byte[] payload = RandomNumberGenerator.GetBytes(12 * 1024 * 1024);
            var frame = new ArrayBufferWriter<byte>();
            ZabbixHeader.Write(frame, payload);
            using var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!, TestContext.Current.CancellationToken);
            NetworkStream stream = client.GetStream();

            await stream.WriteAsync(frame.WrittenMemory, TestContext.Current.CancellationToken);
            ReadOnlyMemory<byte> reply = await ZabbixHeader.ReadAsync(stream, TestContext.Current.CancellationToken).WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken);

            Assert.Equal((12 * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.ASCII.GetString(reply.Span));
        }
        finally
        {
            await listener.StoppingAsync(TestContext.Current.CancellationToken);
            await listener.StopAsync(TestContext.Current.CancellationToken);
            await listener.StoppedAsync(TestContext.Current.CancellationToken);
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public void The_pre_shared_key_handed_to_the_handshake_is_the_decoded_array_itself_and_is_zeroed_after_it()
    {
        byte[] identity = Encoding.UTF8.GetBytes("psk-01");
        var decoded = new List<byte[]>();
        var identities = new ZabbixTlsAcceptor.PskIdentities([(identity, "0123456789abcdef0123456789abcdef")], hex =>
        {
            byte[] key = Convert.FromHexString(hex);
            decoded.Add(key);
            return key;
        });

        byte[]? handed = identities.GetPsk(identity);
        identities.Zero();

        Assert.Same(Assert.Single(decoded), handed);
        Assert.All(decoded[0], value => Assert.Equal(0, value));
    }

    [Fact]
    public void A_second_identity_lookup_zeroes_the_key_handed_out_by_the_first()
    {
        byte[] identity = Encoding.UTF8.GetBytes("psk-01");
        var decoded = new List<byte[]>();
        var identities = new ZabbixTlsAcceptor.PskIdentities([(identity, "0123456789abcdef0123456789abcdef")], hex =>
        {
            byte[] key = Convert.FromHexString(hex);
            decoded.Add(key);
            return key;
        });

        identities.GetPsk(identity);
        identities.GetPsk(identity);
        identities.Zero();

        Assert.Equal(2, decoded.Count);
        Assert.All(decoded, key => Assert.All(key, value => Assert.Equal(0, value)));
    }

    [Fact]
    public void The_default_key_parse_decodes_the_hex_into_the_bytes_it_names()
    {
        byte[] identity = Encoding.UTF8.GetBytes("psk-01");
        var identities = new ZabbixTlsAcceptor.PskIdentities([(identity, "0123456789abcdef0123456789abcdef")]);

        byte[]? handed = identities.GetPsk(identity);

        Assert.Equal(Convert.FromHexString("0123456789abcdef0123456789abcdef"), handed);
        Assert.Null(identities.GetPsk(Encoding.UTF8.GetBytes("someone-else")));
    }

    [Fact]
    public async Task A_listener_that_served_and_refused_connections_holds_no_file_descriptor_after_it_is_disposed()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("counts /proc/self/fd");
        var options = new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0, Timeout = TimeSpan.FromSeconds(5), MaxConnectionsPerPeer = 1 };
        options.AllowedPeers.Add("127.0.0.1");
        var transport = new SocketTransportFactory(Options.Create(new SocketTransportOptions()), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        // Warm the runtime's own descriptors (epoll, the socket engine) before the count, with a listener of its own.
        await RunAsync(options, transport, rounds: 3);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        int before = Directory.GetFiles("/proc/self/fd").Length;

        await RunAsync(options, transport, rounds: 20);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        int after = Directory.GetFiles("/proc/self/fd").Length;
        Assert.True(after <= before, $"{after - before} descriptor(s) left open");
    }

    private static async Task RunAsync(ZabbixListenerOptions options, IConnectionListenerFactory transport, int rounds)
    {
        var listener = new ZabbixListener(options, (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>("{\"response\":\"success\"}"u8.ToArray()), transport);
        await listener.StartingAsync(TestContext.Current.CancellationToken);
        await listener.StartAsync(TestContext.Current.CancellationToken);
        var endPoint = (IPEndPoint)listener.LocalEndPoint!;
        for (int i = 0; i < rounds; i++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endPoint, TestContext.Current.CancellationToken);
            var frame = new ArrayBufferWriter<byte>();
            ZabbixHeader.Write(frame, "{\"request\":\"x\"}"u8);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(frame.WrittenMemory, TestContext.Current.CancellationToken);
            await ZabbixHeader.ReadAsync(stream, TestContext.Current.CancellationToken).WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken);
            using var garbage = new TcpClient();                                                // one that sends garbage and is closed
            await garbage.ConnectAsync(endPoint, TestContext.Current.CancellationToken);
            await garbage.GetStream().WriteAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
            var sink = new byte[16];
            try { while (await garbage.GetStream().ReadAsync(sink, TestContext.Current.CancellationToken).AsTask().WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken) > 0) { } }
            catch (IOException) { /* reset */ }
        }
        await listener.StoppingAsync(TestContext.Current.CancellationToken);
        await listener.StopAsync(TestContext.Current.CancellationToken);
        await listener.StoppedAsync(TestContext.Current.CancellationToken);
        await listener.DisposeAsync();
    }
}
