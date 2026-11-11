// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Community.Zabbix.Listening;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix.Tests;

/// <summary>
/// Certificate mode reads a private key and certificates from files once, into a <see cref="ZabbixCertificateTrust"/>
/// held by the connection that uses it (a <see cref="ZabbixNodeLink"/>, or a listener's acceptor). Whoever builds that
/// connection disposes it, so the key's handle is released when its owner is disposed rather than when the collector
/// gets to it: the sender, getter and server wire built from nodes, and the agent, active agent, proxy and listener hosts.
/// Each test reads the certificate handles themselves (<see cref="ZabbixCertificateTrust.IsReleased"/>), not a flag.
/// </summary>
public sealed class CertificateTrustOwnershipTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("zbx-trust-").FullName;
    private readonly ZabbixTlsOptions _certificate;

    public CertificateTrustOwnershipTests()
    {
        using RSA caKey = RSA.Create(2048);
        var authority = new CertificateRequest("CN=Ownership CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        authority.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        authority.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using X509Certificate2 ca = authority.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Zabbix agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 leaf = request.Create(ca, ca.NotBefore, ca.NotAfter, [1, 2, 3]);   // the CA's own dates: a second clock read could fall past them
        _certificate = new ZabbixTlsOptions
        {
            Mode = ZabbixTlsMode.Certificate,
            CaFile = Write("ca.pem", ca.ExportCertificatePem()),
            CertificateFile = Write("leaf.pem", leaf.ExportCertificatePem()),
            KeyFile = Write("leaf.key", key.ExportPkcs8PrivateKeyPem()),
        };
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task A_wire_client_releases_the_certificates_and_keys_it_read_when_disposed()
    {
        var wire = new ZabbixWireClient(new ZabbixWireClientOptions { Server = $"127.0.0.1:{ClosedPort()}", Connection = new ZabbixConnectionOptions { Tls = _certificate } });
        await Assert.ThrowsAsync<SocketException>(() => wire.PingAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SocketException>(() => wire.GetAgentItemAsync($"127.0.0.1:{ClosedPort()}", "agent.ping", TestContext.Current.CancellationToken));
        ZabbixCertificateTrust[] trust = [.. wire.Links.Select(link => link.Trust!)];
        Assert.Equal(2, trust.Length);
        Assert.All(trust, read => Assert.False(read.IsReleased));

        await wire.DisposeAsync();

        Assert.All(trust, read => Assert.True(read.IsReleased));
    }

    [Fact]
    public async Task A_certificate_file_that_cannot_be_read_fails_a_wire_client_s_first_request_as_its_state()
    {
        var unreadable = new ZabbixTlsOptions { Mode = ZabbixTlsMode.Certificate, CaFile = Path.Combine(_directory, "absent.pem"), CertificateFile = _certificate.CertificateFile, KeyFile = _certificate.KeyFile };
        using var wire = new ZabbixWireClient(new ZabbixWireClientOptions { Server = "127.0.0.1", Connection = new ZabbixConnectionOptions { Tls = unreadable } });

        InvalidOperationException failed = await Assert.ThrowsAsync<InvalidOperationException>(() => wire.PingAsync(TestContext.Current.CancellationToken));

        Assert.IsAssignableFrom<IOException>(failed.InnerException);
    }

    [Fact]
    public async Task A_sender_built_from_nodes_releases_its_certificate_and_key_when_disposed()
    {
        var sender = new ZabbixSender([new ZabbixServerNode("127.0.0.1", ClosedPort())], new ZabbixConnectionOptions { Tls = _certificate });
        ZabbixCertificateTrust trust = sender.Link!.Trust!;
        Assert.False(trust.IsReleased);

        sender.Dispose();

        Assert.True(trust.IsReleased);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sender.SendAsync([new ZabbixValue("web-01", "cpu", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_sender_over_a_given_exchange_owns_nothing_and_its_disposal_leaves_the_exchange_working()
    {
        int calls = 0;
        ZabbixExchangeCallback exchange = (_, _) =>
        {
            calls++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>("""{"response":"success","info":"processed: 1; failed: 0; total: 1; seconds spent: 0.000010"}"""u8.ToArray());
        };
        var sender = new ZabbixSender(exchange, "scripted");

        sender.Dispose();

        Assert.Null(sender.Link);
        // The disposed sender refuses further use, naming itself; the caller's exchange is untouched and still answers.
        ObjectDisposedException disposed = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await sender.SendAsync([new ZabbixValue("web-01", "cpu", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken));
        Assert.Contains(nameof(ZabbixSender), disposed.ObjectName, StringComparison.Ordinal);
        await exchange(ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_getter_built_for_a_node_releases_its_certificate_and_key_when_disposed()
    {
        var getter = new ZabbixGetter(new ZabbixServerNode("127.0.0.1", ClosedPort()), new ZabbixConnectionOptions { Tls = _certificate });
        ZabbixCertificateTrust trust = getter.Link!.Trust!;
        Assert.False(trust.IsReleased);

        getter.Dispose();

        Assert.True(trust.IsReleased);
    }

    [Fact]
    public void A_server_wire_built_for_a_node_releases_its_certificate_and_key_when_disposed()
    {
        var wire = new ZabbixServerWire(new ZabbixServerNode("127.0.0.1", ClosedPort()), "session", new ZabbixConnectionOptions { Tls = _certificate });
        ZabbixCertificateTrust trust = wire.Link!.Trust!;
        Assert.False(trust.IsReleased);

        wire.Dispose();

        Assert.True(trust.IsReleased);
    }

    [Fact]
    public async Task An_agent_host_releases_each_destination_s_certificate_and_key_when_disposed()
    {
        int[] ports = ClosedPorts(2);                                                       // two destinations: two distinct ports
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZabbixPushAgent("edge").AddSource(_ => AsyncEnumerable.Empty<ZabbixValue>())
            .AddServer(new DnsEndPoint("127.0.0.1", ports[0]))
            .AddServer(new DnsEndPoint("127.0.0.1", ports[1]))
            .Configure(o => o.Connection.Tls = _certificate);
        ServiceProvider provider = services.BuildServiceProvider();
        var host = provider.GetRequiredKeyedService<ZabbixPushAgent>("edge");
        await host.HostStartingAsync(TestContext.Current.CancellationToken);
        ZabbixCertificateTrust[] trusts = [.. host.Senders.Select(sender => sender.Link!.Trust!)];
        Assert.Equal(2, trusts.Length);
        Assert.All(trusts, trust => Assert.False(trust.IsReleased));

        await provider.DisposeAsync();

        Assert.All(trusts, trust => Assert.True(trust.IsReleased));
    }

    [Fact]
    public async Task An_active_agent_host_releases_its_certificate_and_key_when_disposed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZabbixActiveAgent("edge").AddServer(new DnsEndPoint("127.0.0.1", ClosedPort())).Configure(o => o.Connection.Tls = _certificate);
        ServiceProvider provider = services.BuildServiceProvider();
        ZabbixActiveAgent agent = provider.GetRequiredKeyedService<ZabbixActiveAgent>("edge");
        await agent.HostStartingAsync(TestContext.Current.CancellationToken);                  // the refresh fails to connect, and is logged
        Hosting.ZabbixActiveAgentDestination host = agent.Sessions[0];
        ZabbixCertificateTrust trust = host.Link!.Trust!;
        Assert.False(trust.IsReleased);

        await provider.DisposeAsync();

        Assert.True(trust.IsReleased);
    }

    [Fact]
    public async Task A_proxy_host_releases_its_uplink_s_certificate_and_key_when_disposed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZabbixActiveProxy("proxy-01").WithServer(new System.Net.DnsEndPoint("127.0.0.1", ClosedPort()))
            .AddTrapper(0, o => o.Address = "127.0.0.1")
            .AllowPeer(System.Net.IPAddress.Loopback)
            .Configure(o => o.Connection.Tls = _certificate);
        ServiceProvider provider = services.BuildServiceProvider();
        ZabbixActiveProxy host = provider.GetRequiredKeyedService<ZabbixActiveProxy>("proxy-01");
        await host.HostStartingAsync(TestContext.Current.CancellationToken);                       // the first pull fails to connect, and is logged
        ZabbixCertificateTrust trust = host.Link!.Trust!;
        Assert.False(trust.IsReleased);

        await provider.DisposeAsync();

        Assert.True(trust.IsReleased);
    }

    [Fact]
    public async Task A_listener_that_accepts_certificates_releases_its_certificate_and_key_when_disposed()
    {
        var options = new ZabbixListenerOptions
        {
            Address = "127.0.0.1",
            Port = 0,
            AllowedPeers = { "127.0.0.1" },
            TlsAccept = ZabbixTlsAcceptModes.Certificate,
            Tls = new ZabbixTlsOptions { CaFile = _certificate.CaFile, CertificateFile = _certificate.CertificateFile, KeyFile = _certificate.KeyFile },
        };
        var listener = new ZabbixListener(options, (_, _) => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty), ListenerTests.Transport());
        await listener.StartingAsync(TestContext.Current.CancellationToken);
        ZabbixCertificateTrust trust = listener.CertificateTrust!;
        Assert.False(trust.IsReleased);

        await listener.DisposeAsync();

        Assert.True(trust.IsReleased);
    }

    private string Write(string name, string pem)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, pem);
        return path;
    }

    // A port nothing listens on: bound, read and released, so a connect to it is refused rather than answered.
    private static int ClosedPort() => ClosedPorts(1)[0];

    // As many such ports, distinct: each is held bound until all are read, so the system cannot hand one out twice.
    private static int[] ClosedPorts(int count)
    {
        var probes = new List<TcpListener>(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probes.Add(probe);
                probe.Start();
            }
            return [.. probes.Select(probe => ((IPEndPoint)probe.LocalEndpoint).Port)];
        }
        finally
        {
            foreach (TcpListener probe in probes) probe.Dispose();
        }
    }
}
