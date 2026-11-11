// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Community.Zabbix.Protocol;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// Certificate mode reads a private key and certificates from the TLS files into the connection a wire command opens
/// (<see cref="ZabbixWireLinks"/>). Every command disposes that connection when its call ends, and an active proxy
/// session when it stops, so the key's handle is released then rather than when the collector gets to it. Each test
/// reads the certificate handles themselves (<see cref="ZabbixCertificateTrust.IsReleased"/>) of the link the command
/// opened to a port of its own, where nothing listens, as the module announces it on its diagnostic listener.
/// </summary>
public sealed class WireTrustReleaseTests : IDisposable, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly string _directory = Directory.CreateTempSubdirectory("zbx-trust-ps-").FullName;
    private readonly string _certificate;
    private readonly int _port = ClosedPort();
    private readonly ConcurrentQueue<ZabbixWireLink> _opened = new();
    private readonly ConcurrentBag<IDisposable> _subscriptions = [];

    public WireTrustReleaseTests()
    {
        using ECDsa caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authority = new CertificateRequest("CN=zbx-trust-ca, O=Zabbix tests", caKey, HashAlgorithmName.SHA256);
        authority.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        authority.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using X509Certificate2 ca = authority.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=zbx-trust-client, O=Zabbix tests", key, HashAlgorithmName.SHA256);
        using X509Certificate2 leaf = request.Create(ca, ca.NotBefore, ca.NotAfter, [1, 2, 3]);
        _certificate = $"-Certificate '{Write("client.crt", leaf.ExportCertificatePem())}' -CertificateKey '{Write("client.key", key.ExportPkcs8PrivateKeyPem())}'"
            + $" -CertificateAuthority '{Write("ca.crt", ca.ExportCertificatePem())}'";
        _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
    }

    public void Dispose()
    {
        foreach (IDisposable subscription in _subscriptions) subscription.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener value)
    {
        if (value.Name == ZabbixWireLinks.DiagnosticName) _subscriptions.Add(value.Subscribe(this));
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
    {
        if (value is { Key: ZabbixWireLinks.LinkOpened, Value: ZabbixWireLink link }) _opened.Enqueue(link);
    }

    void IObserver<DiagnosticListener>.OnCompleted()
    {
    }

    void IObserver<DiagnosticListener>.OnError(Exception error)
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnCompleted()
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error)
    {
    }

    [Theory]
    [InlineData("Send-ZbxData web-01 agent.ping 1 -Server 127.0.0.1:{port} {tls} -Confirm:$false")]
    [InlineData("Get-ZbxAgentItem 127.0.0.1:{port} agent.ping, agent.version {tls}")]
    [InlineData("Receive-ZbxProxyConfig zbx-proxy 127.0.0.1:{port} {tls}")]
    [InlineData("New-ZbxHistoryValue -ItemId 42 -Value 7 | Send-ZbxProxyData zbx-proxy 127.0.0.1:{port} {tls} -Confirm:$false")]
    public void A_wire_command_releases_its_certificate_and_key_when_its_call_ends(string command)
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke(command.Replace("{port}", Port(), StringComparison.Ordinal).Replace("{tls}", _certificate, StringComparison.Ordinal));

        Assert.NotEmpty(run.Errors);                                                                 // nothing listens: the exchange fails
        ZabbixWireLink link = Assert.Single(Mine());
        Assert.True(link.Trust!.IsReleased);
    }

    [Theory]
    [InlineData("Get-ZbxServerStatus -Server 127.0.0.1:{port} {tls}")]
    [InlineData("Get-ZbxQueue -Server 127.0.0.1:{port} {tls}")]
    [InlineData("Get-ZbxQueue -Server 127.0.0.1:{port} -Details {tls}")]
    [InlineData("Test-ZbxExpression 'last(/web-01/agent.ping)=1' -Server 127.0.0.1:{port} {tls}")]
    [InlineData("Test-ZbxItem -Data @{ item = @{ type = 0; key = 'agent.ping' } } -Server 127.0.0.1:{port} {tls}")]
    public async Task A_server_wire_command_releases_its_certificate_and_key_when_its_call_ends(string command)
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(api);

        Run run = shell.Invoke(command.Replace("{port}", Port(), StringComparison.Ordinal).Replace("{tls}", _certificate, StringComparison.Ordinal));

        Assert.NotEmpty(run.Errors);
        ZabbixWireLink link = Assert.Single(Mine());
        Assert.True(link.Trust!.IsReleased);
    }

    [Fact]
    public void An_active_proxy_session_holds_its_certificate_and_key_until_it_stops()
    {
        using Shell shell = Shell.Open();
        Run started = shell.Invoke($"Start-ZbxActiveProxy probe zbx-proxy 127.0.0.1:{Port()} {_certificate} -Confirm:$false");
        try
        {
            Assert.Empty(started.Errors);
            ZabbixWireLink link = Assert.Single(Mine());
            Assert.False(link.Trust!.IsReleased);

            shell.Invoke("Stop-ZbxActiveProxy probe -Confirm:$false");

            Assert.True(link.Trust.IsReleased);
        }
        finally
        {
            shell.Invoke("Stop-ZbxActiveProxy probe -Confirm:$false -ErrorAction SilentlyContinue");
        }
    }

    // The links opened to this test's own port: tests run in parallel, and every link is announced to every listener.
    private ZabbixWireLink[] Mine() => [.. _opened.Where(link => link.First.Port == _port)];

    private string Port() => _port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private string Write(string name, string pem)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, pem);
        return path;
    }

    // A port nothing listens on: bound, read and released, so a connect to it is refused rather than answered.
    private static int ClosedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
