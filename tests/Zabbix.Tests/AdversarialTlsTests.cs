// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Community.Zabbix.Listening;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Community.Zabbix.Tests;

/// <summary>
/// TLS and PSK against wrong words and hostile peers — a wrong identity, a wrong key, keys of every
/// malformed length (upstream's limits: <c>HOST_TLS_PSK_LEN_MIN</c> 32 and <c>HOST_TLS_PSK_LEN</c> 512 hex digits,
/// <c>HOST_TLS_PSK_IDENTITY_LEN</c> 512 bytes, <c>include/zbxcommon.h</c>, checked in <c>src/libs/zbxcomms/tls.c</c>), a
/// plaintext peer to a TLS listener and the reverse, garbage after the handshake, a pinned issuer that does not match. Every
/// refusal must be the designed one, and no key material may reach a log record, a structured value or an exception.
/// </summary>
[Collection("Adversarial wire stress")]
public sealed class AdversarialTlsTests : IDisposable
{
    private const string Key = "1f87b595725ac58dd977beef14b97461a7c1045b9a1c963065002c5473194952";
    private const string WrongKey = "c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00";
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(10);

    private readonly string _directory = Directory.CreateTempSubdirectory("zbx-adv-tls-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static async Task<(ZabbixListener Listener, ZabbixServerNode Node)> StartAsync(Action<ZabbixListenerOptions> configure, ILogger<ZabbixListener>? logger = null, IMeterFactory? meters = null, TimeProvider? time = null)
    {
        var options = new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0, Timeout = TimeSpan.FromSeconds(5) };
        options.AllowedPeers.Add("127.0.0.1");
        configure(options);
        using ILoggerFactory? loggers = SingleLoggerFactory.Of(logger);
        var listener = new ZabbixListener(options, static (request, _) => ValueTask.FromResult(request.Payload), ListenerTests.Transport(), loggers, time, meters);
        await listener.StartingAsync(TestContext.Current.CancellationToken);
        await listener.StartAsync(TestContext.Current.CancellationToken);
        var bound = (IPEndPoint)listener.LocalEndPoint!;
        return (listener, new ZabbixServerNode(bound.Address.ToString(), bound.Port));
    }

    private static async Task StopAsync(ZabbixListener listener)
    {
        await listener.StoppingAsync(TestContext.Current.CancellationToken);
        using var patience = new CancellationTokenSource(Failsafe, TimeProvider.System);
        await listener.StopAsync(patience.Token);
        await listener.StoppedAsync(TestContext.Current.CancellationToken);
        await listener.DisposeAsync();
    }

    private static ZabbixExchangeCallback PskClient(ZabbixServerNode node, string identity, string key, ILogger? logger = null)
        => ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = identity, Psk = key } }, logger: logger);

    // Every rendering a record or an exception has: the message, the exception chain, the structured values.
    private static IEnumerable<string> Rendered(FakeLogCollector logs, params Exception?[] thrown)
        => logs.GetSnapshot()
            .SelectMany(record => new[] { record.Message, record.Exception?.ToString() }.Concat(record.StructuredState?.Select(pair => pair.Value) ?? []))
            .Concat(thrown.Select(exception => exception?.ToString()))
            .OfType<string>();

    private static void AssertNoKey(IEnumerable<string> rendered, params string[] keys)
    {
        foreach (string key in keys)
            Assert.DoesNotContain(rendered, text => text.Contains(key, StringComparison.OrdinalIgnoreCase) || text.Contains(Convert.ToBase64String(Convert.FromHexString(key)), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_known_identity_with_the_wrong_key_is_refused_by_the_handshake_and_neither_key_is_rendered()
    {
        var serverLogs = new FakeLogCollector();
        var clientLogs = new FakeLogCollector();
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(o => { o.TlsAccept = ZabbixTlsAcceptModes.Psk; o.PreSharedKeys["edge-01"] = Key; }, new FakeLogger<ZabbixListener>(serverLogs));
        Exception failure;
        try
        {
            failure = await Assert.ThrowsAnyAsync<Exception>(async () => await PskClient(node, "edge-01", WrongKey, new FakeLogger(clientLogs))("secret"u8.ToArray(), TestContext.Current.CancellationToken));
        }
        finally { await StopAsync(listener); }

        Assert.True(failure is AuthenticationException or IOException, failure.GetType().Name);
        Assert.Contains(serverLogs.GetSnapshot(), record => record.Id.Id == 6002 && record.Exception is AuthenticationException);
        AssertNoKey(Rendered(serverLogs, failure).Concat(Rendered(clientLogs)), Key, WrongKey);
    }

    [Fact]
    public async Task An_unknown_identity_is_refused_and_the_configured_key_is_not_rendered()
    {
        var serverLogs = new FakeLogCollector();
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(o => { o.TlsAccept = ZabbixTlsAcceptModes.Psk; o.PreSharedKeys["edge-01"] = Key; }, new FakeLogger<ZabbixListener>(serverLogs));
        Exception failure;
        try
        {
            failure = await Assert.ThrowsAnyAsync<Exception>(async () => await PskClient(node, "stranger", Key)("secret"u8.ToArray(), TestContext.Current.CancellationToken));
        }
        finally { await StopAsync(listener); }

        Assert.True(failure is AuthenticationException or IOException, failure.GetType().Name);
        AssertNoKey(Rendered(serverLogs, failure), Key);
    }

    [Fact]
    public async Task A_tls_client_to_a_listener_that_accepts_only_unencrypted_is_refused_and_counted_as_tls_not_allowed()
    {
        using ServiceProvider metrics = new ServiceCollection().AddMetrics().BuildServiceProvider();
        using var rejected = new MetricCollector<long>(metrics.GetRequiredService<IMeterFactory>(), ZabbixListenerTelemetry.Name, ZabbixListenerTelemetry.RejectedConnections);
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(o => o.TlsAccept = ZabbixTlsAcceptModes.Unencrypted, meters: metrics.GetRequiredService<IMeterFactory>());
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(async () => await PskClient(node, "edge-01", Key)("secret"u8.ToArray(), TestContext.Current.CancellationToken));
        }
        finally { await StopAsync(listener); }

        CollectedMeasurement<long> refusal = Assert.Single(rejected.GetMeasurementSnapshot());
        Assert.Equal("tls_not_allowed", refusal.Tags[ZabbixListenerTelemetry.RejectionTag]);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcde")]                                               // 31: odd
    [InlineData("0123456789abcdef0123456789abcd")]                                                // 30: too short
    [InlineData("0123456789abcdef0123456789abcdeg")]                                              // not hex
    [InlineData("0123456789abcdef 0123456789abcdef")]                                             // a space inside
    [InlineData("0x0123456789abcdef0123456789abcdef")]                                            // a prefix
    public void A_malformed_key_is_a_format_exception_whose_message_does_not_repeat_the_key(string hex)
    {
        FormatException refused = Assert.ThrowsAny<FormatException>(() => ZabbixTls.ParsePsk(hex));

        Assert.DoesNotContain(hex, refused.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_key_of_exactly_512_hex_digits_is_upstream_s_longest_and_is_accepted()
        => Assert.Equal(256, ZabbixTls.ParsePsk(string.Concat(Enumerable.Repeat("ab", 256))).Length);

    // Upstream refuses a key longer than HOST_TLS_PSK_LEN (512 hex digits, 256 bytes: zbx_read_psk_file in tls.c, "PSK in
    // file ... is too long") and an identity longer than HOST_TLS_PSK_IDENTITY_LEN (512 bytes: tls.c:34,
    // zbx_check_psk_identity_len), so ParsePsk checks the maximum as well as the minimum and the parity, and
    // ZabbixTlsOptions and ZabbixListenerOptions check the identity's length, not only its emptiness: a 514-digit key must
    // not parse to 257 bytes, nor a 513-byte identity validate. The bound is 512 bytes on 7.0, 7.4 and 8.0 alike (TlsTests).
    [Fact]
    public void A_key_longer_than_upstream_s_512_hex_digits_is_refused()
        => Assert.ThrowsAny<FormatException>(() => ZabbixTls.ParsePsk(string.Concat(Enumerable.Repeat("ab", 257))));

    [Fact]
    public void An_identity_longer_than_upstream_s_512_bytes_is_refused_at_validation()
    {
        var options = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = new string('i', 513), Psk = Key };

        List<ValidationResult> failures = [.. options.Validate(new ValidationContext(options))];

        Assert.NotEmpty(failures);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void A_client_with_a_malformed_key_fails_validation_without_repeating_the_key(string bad)
    {
        var options = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = "edge-01", Psk = bad };

        List<ValidationResult> failures = [.. options.Validate(new ValidationContext(options))];

        ValidationResult failure = Assert.Single(failures);
        Assert.DoesNotContain(bad, failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void A_listener_whose_table_holds_a_malformed_key_fails_validation_without_repeating_the_key(string bad)
    {
        var options = new ZabbixListenerOptions { TlsAccept = ZabbixTlsAcceptModes.Psk };
        options.AllowedPeers.Add("127.0.0.1");
        options.PreSharedKeys["edge-01"] = bad;

        List<ValidationResult> failures = [.. options.Validate(new ValidationContext(options))];

        ValidationResult failure = Assert.Single(failures);
        Assert.DoesNotContain(bad, failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A BouncyCastle PSK client of the test's own, so the test can reach under the TLS stream afterwards.</summary>
    private sealed class RawPskClient(string identity, string key)
        : PskTlsClient(new BcTlsCrypto(new SecureRandom()), new BasicTlsPskIdentity(Encoding.UTF8.GetBytes(identity), Convert.FromHexString(key)))
    {
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv12];

        protected override int[] GetSupportedCipherSuites() => [CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256];
    }

    [Fact]
    public async Task Garbage_on_the_socket_after_a_completed_psk_handshake_closes_the_connection_and_the_handler_never_runs()
    {
        var logs = new FakeLogCollector();
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(o => { o.TlsAccept = ZabbixTlsAcceptModes.Psk; o.PreSharedKeys["edge-01"] = Key; }, new FakeLogger<ZabbixListener>(logs));
        int read;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, node.Port, TestContext.Current.CancellationToken);
            NetworkStream raw = client.GetStream();
            var protocol = new TlsClientProtocol(raw);
            await Task.Run(() => protocol.Connect(new RawPskClient("edge-01", Key)), TestContext.Current.CancellationToken);

            await raw.WriteAsync("\x17\x03\x03\x00\x20this is not a sealed TLS record!"u8.ToArray(), TestContext.Current.CancellationToken);   // application-data type, junk inside
            var buffer = new byte[256];
            try
            {
                do read = await raw.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask().WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken);
                while (read > 0);                                                                  // an alert may come first; then the close
            }
            catch (IOException) { read = 0; }
        }
        finally { await StopAsync(listener); }

        Assert.Equal(0, read);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 6002);
        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Id.Id is 6004 or 6005);
        AssertNoKey(Rendered(logs), Key);
    }

    [Fact]
    public async Task A_tls_hello_whose_record_never_completes_on_a_psk_and_cert_listener_is_cut_at_the_deadline()
    {
        (string certificate, string key, string ca) = Pki("CN=Adversarial CA", "CN=listener");
        var time = new FakeTimeProvider();
        var logs = new FakeLogCollector();
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(o =>
        {
            o.TlsAccept = ZabbixTlsAcceptModes.Psk | ZabbixTlsAcceptModes.Certificate;
            o.Tls = new ZabbixTlsOptions { CaFile = ca, CertificateFile = certificate, KeyFile = key };
            o.PreSharedKeys["edge-01"] = Key;
        }, new FakeLogger<ZabbixListener>(logs), time: time);
        int read;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, node.Port, TestContext.Current.CancellationToken);
            await client.GetStream().WriteAsync(new byte[] { 0x16, 0x03, 0x01, 0xFF, 0xFF, 0x01 }, TestContext.Current.CancellationToken);   // declares 65 535, sends 1
            while (listener.InFlight != 1) await Task.Yield();
            time.Advance(TimeSpan.FromSeconds(5));
            read = await ListenerCuts.ReadOrResetAsync(client.GetStream()).WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken);
        }
        finally { await StopAsync(listener); }

        Assert.Equal(0, read);
        Assert.Contains(logs.GetSnapshot(), record => ListenerRefusals.Is(record, ZabbixListenerRejection.Timeout));
    }

    // ---- certificates -----------------------------------------------------------------------------------------------------

    private (string Certificate, string Key, string Ca) Pki(string caName, string leafName, string? prefix = null)
    {
        prefix ??= Guid.NewGuid().ToString("N");
        using RSA caKey = RSA.Create(2048);
        var caRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest(caName, caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        using X509Certificate2 ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return Leaf(ca, leafName, prefix);
    }

    private (string Certificate, string Key, string Ca) Leaf(X509Certificate2 ca, string leafName, string prefix)
    {
        using RSA key = RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(leafName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));
        using X509Certificate2 leaf = request.Create(ca, ca.NotBefore, ca.NotAfter, RandomNumberGenerator.GetBytes(8));   // the CA's own dates: a second clock read could fall past them
        string caFile = Path.Combine(_directory, prefix + "-ca.pem");
        string certificateFile = Path.Combine(_directory, prefix + ".pem");
        string keyFile = Path.Combine(_directory, prefix + ".key");
        File.WriteAllText(caFile, ca.ExportCertificatePem());
        File.WriteAllText(certificateFile, leaf.ExportCertificatePem());
        File.WriteAllText(keyFile, key.ExportPkcs8PrivateKeyPem());
        return (certificateFile, keyFile, caFile);
    }

    private (string ServerCertificate, string ServerKey, string ClientCertificate, string ClientKey, string Ca) SharedPki()
    {
        using RSA caKey = RSA.Create(2048);
        var caRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Adversarial CA,O=Zabbix SIA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        using X509Certificate2 ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        (string serverCertificate, string serverKey, string caFile) = Leaf(ca, "CN=listener", "server");
        (string clientCertificate, string clientKey, _) = Leaf(ca, "CN=agent", "client");
        return (serverCertificate, serverKey, clientCertificate, clientKey, caFile);
    }

    [Fact]
    public async Task A_cert_listener_refuses_a_peer_whose_issuer_is_not_the_pinned_one()
    {
        (string serverCertificate, string serverKey, string clientCertificate, string clientKey, string ca) = SharedPki();
        var logs = new FakeLogCollector();
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(o =>
        {
            o.TlsAccept = ZabbixTlsAcceptModes.Certificate;
            o.Tls = new ZabbixTlsOptions { CaFile = ca, CertificateFile = serverCertificate, KeyFile = serverKey, PeerCertificateIssuer = "CN=Some Other CA,O=Zabbix SIA" };
        }, new FakeLogger<ZabbixListener>(logs));
        Exception failure;
        try
        {
            ZabbixExchangeCallback exchange = ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.Certificate, CaFile = ca, CertificateFile = clientCertificate, KeyFile = clientKey } });
            failure = await Assert.ThrowsAnyAsync<Exception>(async () => await exchange("pinned"u8.ToArray(), TestContext.Current.CancellationToken));
        }
        finally { await StopAsync(listener); }

        Assert.True(ZabbixFrames.IsExchangeFailure(failure), failure.GetType().Name);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 6002 && record.Exception is AuthenticationException);
        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Id.Id == 6004);
    }

    // On the listener the certificate check's reason ("issuer \"…\" is not the pinned \"…\"", an untrusted chain, a
    // revoked peer) is logged, as upstream logs the reason on the refusing side (tls_openssl.c,
    // zbx_check_server_issuer_subject) and as the client side logs the same refusal (1103). Written to NullLogger, the
    // listener would log only 6002 with SslStream's generic AuthenticationException ("The remote certificate was rejected
    // by the provided RemoteCertificateValidationCallback"), and no record would name the issuer.
    [Fact]
    public async Task A_cert_listener_that_refuses_a_pinned_issuer_logs_why()
    {
        (string serverCertificate, string serverKey, string clientCertificate, string clientKey, string ca) = SharedPki();
        var logs = new FakeLogCollector();
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(o =>
        {
            o.TlsAccept = ZabbixTlsAcceptModes.Certificate;
            o.Tls = new ZabbixTlsOptions { CaFile = ca, CertificateFile = serverCertificate, KeyFile = serverKey, PeerCertificateIssuer = "CN=Some Other CA,O=Zabbix SIA" };
        }, new FakeLogger<ZabbixListener>(logs));
        try
        {
            ZabbixExchangeCallback exchange = ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.Certificate, CaFile = ca, CertificateFile = clientCertificate, KeyFile = clientKey } });
            await Assert.ThrowsAnyAsync<Exception>(async () => await exchange("pinned"u8.ToArray(), TestContext.Current.CancellationToken));
        }
        finally { await StopAsync(listener); }

        Assert.Contains(Rendered(logs), text => text.Contains("CN=Some Other CA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cert_listener_serves_a_peer_whose_issuer_is_the_pinned_one()
    {
        (string serverCertificate, string serverKey, string clientCertificate, string clientKey, string ca) = SharedPki();
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(o =>
        {
            o.TlsAccept = ZabbixTlsAcceptModes.Certificate;
            o.Tls = new ZabbixTlsOptions { CaFile = ca, CertificateFile = serverCertificate, KeyFile = serverKey, PeerCertificateIssuer = ZabbixCertificateName.Format(new X500DistinguishedName("CN=Adversarial CA,O=Zabbix SIA")) };
        });
        ReadOnlyMemory<byte> reply;
        try
        {
            ZabbixExchangeCallback exchange = ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.Certificate, CaFile = ca, CertificateFile = clientCertificate, KeyFile = clientKey } });
            reply = await exchange("pinned"u8.ToArray(), TestContext.Current.CancellationToken);
        }
        finally { await StopAsync(listener); }

        Assert.Equal("pinned", Encoding.UTF8.GetString(reply.Span));
    }

    [Fact]
    public async Task A_plaintext_peer_to_a_cert_listener_is_refused_before_any_handshake_and_without_a_reply()
    {
        (string certificate, string key, string ca) = Pki("CN=Adversarial CA", "CN=listener");
        var logs = new FakeLogCollector();
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(o =>
        {
            o.TlsAccept = ZabbixTlsAcceptModes.Certificate;
            o.Tls = new ZabbixTlsOptions { CaFile = ca, CertificateFile = certificate, KeyFile = key };
        }, new FakeLogger<ZabbixListener>(logs));
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(async () => await ZabbixFrames.Connect(node)("plain"u8.ToArray(), TestContext.Current.CancellationToken));
        }
        finally { await StopAsync(listener); }

        Assert.Contains(logs.GetSnapshot(), record => ListenerRefusals.Is(record, ZabbixListenerRejection.UnencryptedNotAllowed));
    }

    [Fact]
    public async Task A_psk_client_against_a_plaintext_server_fails_as_a_handshake_failure_the_exchange_recognises()
    {
        await using var peer = new FramesTests.Peer(FramesTests.Peer.Answer);                    // speaks plain frames only
        var logs = new FakeLogCollector();

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(async () => await PskClient(peer.Node, "edge-01", Key, new FakeLogger(logs))("secret"u8.ToArray(), TestContext.Current.CancellationToken));

        Assert.True(ZabbixFrames.IsExchangeFailure(failure), failure.GetType().FullName);
        AssertNoKey(Rendered(logs, failure), Key);
    }
}
