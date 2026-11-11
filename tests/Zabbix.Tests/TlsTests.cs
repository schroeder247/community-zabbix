// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using CertificateRequest = System.Security.Cryptography.X509Certificates.CertificateRequest;

namespace Community.Zabbix.Tests;

/// <summary>
/// TLS as upstream's <c>tls_openssl.c</c> does it: the CA file is the only trust anchor, the host name is not checked, the
/// issuer/subject pins are the RFC 2253 string OpenSSL prints (ordinal), revocation only against a CRL file, and a
/// pre-shared-key handshake that stalls is ended by the deadline.
/// </summary>
public sealed class TlsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("zbx-wire-tls-").FullName;
    private readonly List<X509Certificate2> _authorities = [];                              // the CAs a test's PKI issued from

    public void Dispose()
    {
        foreach (X509Certificate2 authority in _authorities) authority.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>A CA and a certificate it issued, written as PEM files.</summary>
    private sealed record Issued(X509Certificate2 Ca, X509Certificate2 Leaf, string CaFile, string CertificateFile, string KeyFile);

    private static X509Certificate2 CreateCa(string name, out RSA key)
    {
        key = RSA.Create(2048);
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private Issued Issue(string prefix, X509Certificate2 ca, X500DistinguishedName subject, bool serverAuth, byte serial = 1)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([serverAuth ? new Oid("1.3.6.1.5.5.7.3.1") : new Oid("1.3.6.1.5.5.7.3.2")], false));
        // An unreachable distribution point: fetching it would stall or fail, so passing proves nothing was fetched.
        request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(["http://127.0.0.1:9/ca.crl"]));
        X509Certificate2 leaf = request.Create(ca, ca.NotBefore, ca.NotAfter, new byte[] { serial, 7, 7 });   // the CA's own dates: a second clock read could fall past them
        string caFile = Path.Combine(_directory, prefix + "-ca.pem");
        string certificateFile = Path.Combine(_directory, prefix + ".pem");
        string keyFile = Path.Combine(_directory, prefix + ".key");
        File.WriteAllText(caFile, ca.ExportCertificatePem());
        File.WriteAllText(certificateFile, leaf.ExportCertificatePem());
        File.WriteAllText(keyFile, key.ExportPkcs8PrivateKeyPem());
        return new Issued(ca, leaf, caFile, certificateFile, keyFile);
    }

    private static readonly X500DistinguishedName ServerName = new("CN=Zabbix server, OU=Development group, O=Zabbix SIA, L=Riga, S=Riga, C=LV");

    /// <summary>A certificate-mode server on loopback that echoes one frame, with its own words.</summary>
    private static (ZabbixServerNode Node, Task Served) Serve(ZabbixTlsOptions words)
    {
        var port = new StrongBox<int>();
        Task served = ServeAsync(port, words);                                              // bound before its first await
        return (new ZabbixServerNode("127.0.0.1", port.Value), served);
    }

    private static async Task ServeAsync(StrongBox<int> port, ZabbixTlsOptions words)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port.Value = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            using ZabbixCertificateTrust trust = ZabbixCertificateTrust.Load(words, TimeProvider.System);
            await using Stream secured = await ZabbixTls.WrapServerAsync(client.GetStream(), trust, "client", NullLogger.Instance);
            ReadOnlyMemory<byte> request = await ZabbixHeader.ReadAsync(secured);
            await ZabbixHeader.WriteAsync(secured, request);
        }
        catch (Exception failure) when (failure is AuthenticationException or IOException or InvalidDataException) { }
    }

    // The CA keeps its key (the CRL tests sign with it); the key object lives as long as the certificate that holds it.
    private (ZabbixTlsOptions Server, Issued ServerIssued, Issued ClientIssued) Pki()
    {
        X509Certificate2 ca = CreateCa("CN=Zabbix CA", out _);
        _authorities.Add(ca);
        Issued server = Issue("server", ca, ServerName, serverAuth: true);
        Issued client = Issue("client", ca, new X500DistinguishedName("CN=Zabbix agent"), serverAuth: false, serial: 2);
        var words = new ZabbixTlsOptions { Mode = ZabbixTlsMode.Certificate, CaFile = server.CaFile, CertificateFile = server.CertificateFile, KeyFile = server.KeyFile };
        return (words, server, client);
    }

    private static ZabbixTlsOptions Client(Issued client, string? caFile = null) => new()
    {
        Mode = ZabbixTlsMode.Certificate,
        CaFile = caFile ?? client.CaFile,
        CertificateFile = client.CertificateFile,
        KeyFile = client.KeyFile,
    };

    // Upstream sets a TLS 1.2 minimum on its certificate context (tls_openssl.c:936, 7.0/7.4/8.0 alike). SslProtocols
    // has no minimum, only a set, and None leaves the choice to the OS, which negotiates 1.0/1.1 where the host still
    // enables them. The observable (a 1.1-only peer refused) cannot be staged here: this host's OpenSSL 3 refuses 1.1 itself at its
    // default security level, so the set the SDK hands SslStream is what is pinned.
    [Fact]
    public void Certificate_mode_offers_tls_1_2_and_1_3_as_upstream_s_minimum_admits()
    {
#pragma warning disable CA5398 // the pin under test is the hard-coded set
        Assert.Equal(SslProtocols.Tls12 | SslProtocols.Tls13, ZabbixTls.CertificateProtocols);
#pragma warning restore CA5398
    }

    // The identity bound is HOST_TLS_PSK_IDENTITY_LEN, 512 bytes, the same on every server
    // (include/zbxcommon.h 7.0:625, 7.4:657, 8.0:685, "for up to 128 UTF-8 characters"; enforced in bytes by
    // zbx_check_psk_identity_len, tls.c:34 in all three). 128 is the character width of the hosts/proxy
    // tls_psk_identity columns (schema.tmpl varchar(128)), which only an identity the server stores is held to: a C
    // agent's TLSPSKIdentity, which the getter presents, is bound by the 512 bytes alone. So the SDK checks the bytes.
    [Theory]
    [InlineData("7.0")]
    [InlineData("7.4")]
    [InlineData("8.0")]
    public void A_psk_identity_is_bound_to_512_utf8_bytes_on_every_server_not_to_128_characters(string version)
    {
        _ = version;                                                                          // the bound is the same in all three
        static bool Valid(string identity)
        {
            var options = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = identity, Psk = "1f87b595725ac58dd977beef14b97461" };
            return !options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)).Any();
        }
        string fourBytes = char.ConvertFromUtf32(0x1F600);

        Assert.True(Valid(string.Concat(Enumerable.Repeat(fourBytes, 128))));                  // 128 characters, 512 bytes
        Assert.False(Valid(string.Concat(Enumerable.Repeat(fourBytes, 128)) + "i"));          // 513 bytes
        Assert.True(Valid(new string('i', 300)));                                             // past 128 characters, within 512 bytes
    }

    [Fact]
    public async Task A_peer_signed_by_the_configured_ca_is_trusted_and_its_host_name_is_not_checked()
    {
        (ZabbixTlsOptions server, _, Issued client) = Pki();
        (ZabbixServerNode node, Task served) = Serve(server);                              // the certificate says "Zabbix server", not 127.0.0.1

        ReadOnlyMemory<byte> echo = await ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Tls = Client(client) })("hello"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal("hello", Encoding.UTF8.GetString(echo.Span));
        await served;
    }

    [Fact]
    public async Task A_peer_from_a_ca_other_than_the_configured_one_is_refused_and_the_refusal_is_logged()
    {
        (ZabbixTlsOptions server, _, Issued client) = Pki();
        using X509Certificate2 other = CreateCa("CN=Other CA", out RSA otherKey);
        otherKey.Dispose();
        string otherFile = Path.Combine(_directory, "other-ca.pem");
        await File.WriteAllTextAsync(otherFile, other.ExportCertificatePem(), TestContext.Current.CancellationToken);
        (ZabbixServerNode node, Task served) = Serve(server);
        var logs = new FakeLogCollector();

        await Assert.ThrowsAsync<AuthenticationException>(async () =>
            await ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Tls = Client(client, otherFile) }, logger: new FakeLogger(logs))("hello"u8.ToArray(), TestContext.Current.CancellationToken));

        FakeLogRecord refused = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 1103);
        Assert.Equal(LogLevel.Warning, refused.Level);
        Assert.Contains("only trust anchor", refused.Message, StringComparison.Ordinal);
        await served;
    }

    [Theory]
    [InlineData("CN=Zabbix server,OU=Development group,O=Zabbix SIA,L=Riga,ST=Riga,C=LV", true)]
    [InlineData("CN=Zabbix server, OU=Development group, O=Zabbix SIA, L=Riga, ST=Riga, C=LV", false)]   // .NET's Name spacing
    [InlineData("C=LV,ST=Riga,L=Riga,O=Zabbix SIA,OU=Development group,CN=Zabbix server", false)]        // encoded order
    [InlineData("cn=Zabbix server,ou=Development group,o=Zabbix SIA,l=Riga,st=Riga,c=LV", false)]        // strcmp is case-sensitive
    public async Task A_subject_pin_is_compared_with_the_string_openssl_prints(string pin, bool accepted)
    {
        (ZabbixTlsOptions server, _, Issued client) = Pki();
        (ZabbixServerNode node, Task served) = Serve(server);
        ZabbixTlsOptions words = Client(client);
        words.PeerCertificateSubject = pin;
        words.PeerCertificateIssuer = "CN=Zabbix CA";
        var logs = new FakeLogCollector();
        ZabbixExchangeCallback exchange = ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Tls = words }, logger: new FakeLogger(logs));

        Exception? failure = await Record.ExceptionAsync(async () => await exchange("pin"u8.ToArray(), TestContext.Current.CancellationToken));

        Assert.Equal(accepted, failure is null);
        if (!accepted)
            Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1103 && record.Message.Contains("is not the pinned", StringComparison.Ordinal)
                                                        && record.Message.Contains("CN=Zabbix server,OU=Development group,O=Zabbix SIA,L=Riga,ST=Riga,C=LV", StringComparison.Ordinal));
        await served;
    }

    [Fact]
    public void Certificate_mode_without_a_ca_file_is_refused_when_the_exchange_is_built()
    {
        var words = new ZabbixTlsOptions { Mode = ZabbixTlsMode.Certificate, CertificateFile = "c.pem", KeyFile = "c.key" };

        ArgumentException refused = Assert.Throws<ArgumentException>(() => ZabbixFrames.Over([new ZabbixServerNode("127.0.0.1")], new ZabbixConnectionOptions { Tls = words }));

        Assert.Contains("CaFile", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_with_certificate_tls_and_no_ca_file_fails_at_start()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Community.Zabbix.ZabbixPushAgentServiceCollectionExtensions.AddZabbixPushAgent(services, "edge").AddServer("127.0.0.1").Configure(options =>
            options.Connection.Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.Certificate, CertificateFile = "c.pem", KeyFile = "c.key" });
        using Microsoft.Extensions.DependencyInjection.ServiceProvider provider = services.BuildValidated();

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.Options.IStartupValidator>(provider).Validate());
    }

    // ----- the name upstream prints ---------------------------------------------------------------------------

    [Fact]
    public void A_name_is_printed_as_openssl_prints_it_with_rfc2253_escapes_short_names_and_unknown_attributes_in_hex()
    {
        // Measured: `openssl x509 -noout -subject -nameopt RFC2253,-esc_msb` on OpenSSL 3.6.4 for this exact name.
        var builder = new X500DistinguishedNameBuilder();
        builder.AddCountryOrRegion("LV");
        builder.AddStateOrProvinceName("Riga");
        builder.AddLocalityName("Riga");
        builder.AddOrganizationName("Zabbix SIA");
        builder.AddOrganizationalUnitName("Development group");
        builder.AddCommonName("Zabbix proxy, \"q\" #1 +x");
        builder.AddEmailAddress("a@b.c");
        builder.AddDomainComponent("example");
        builder.Add("2.5.4.97", "NTRLV-1");
        builder.Add("1.2.3.4.5", "odd");
        builder.AddCommonName("Ünïcode ");

        string printed = ZabbixCertificateName.Format(builder.Build());

        Assert.Equal("C=LV,ST=Riga,L=Riga,O=Zabbix SIA,OU=Development group,CN=Zabbix proxy\\, \\\"q\\\" #1 \\+x,emailAddress=a@b.c,DC=example,organizationIdentifier=NTRLV-1,1.2.3.4.5=#0C036F6464,CN=Ünïcode\\ ", printed);
    }

    [Fact]
    public void A_multi_valued_rdn_is_printed_reversed_and_joined_by_a_plus_as_openssl_prints_it()
    {
        // `openssl req -subj "/C=LV/ST=Riga/O=Zabbix SIA/OU=Dev+UID=7/CN=Zabbix proxy"`, printed by OpenSSL 3.6.4 with
        // `-nameopt RFC2253,-esc_msb` as the expected string below; the same name encoded here (DER sorts the SET).
        var writer = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            foreach ((string Oid, string Value)[] rdn in new[]
            {
                new[] { ("2.5.4.6", "LV") }, [("2.5.4.8", "Riga")], [("2.5.4.10", "Zabbix SIA")],
                [("2.5.4.11", "Dev"), ("0.9.2342.19200300.100.1.1", "7")], [("2.5.4.3", "Zabbix proxy")],
            })
            {
                using (writer.PushSetOf())
                {
                    foreach ((string oid, string value) in rdn)
                    {
                        using (writer.PushSequence())
                        {
                            writer.WriteObjectIdentifier(oid);
                            writer.WriteCharacterString(oid == "2.5.4.6" ? System.Formats.Asn1.UniversalTagNumber.PrintableString : System.Formats.Asn1.UniversalTagNumber.UTF8String, value);
                        }
                    }
                }
            }
        }

        Assert.Equal("CN=Zabbix proxy,UID=7+OU=Dev,O=Zabbix SIA,ST=Riga,C=LV", ZabbixCertificateName.Format(new X500DistinguishedName(writer.Encode())));
    }

    // ----- revocation (TLSCRLFile) ----------------------------------------------------------------------------

    private static X509Chain Chain(X509Certificate2 ca, X509Certificate2 leaf)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(chain.Build(leaf));
        return chain;
    }

    private static string Crl(X509Certificate2 ca, DateTimeOffset now, TimeSpan validFor, params X509Certificate2[] revoked)
    {
        var builder = new CertificateRevocationListBuilder();
        foreach (X509Certificate2 certificate in revoked) builder.AddEntry(certificate, now.AddMinutes(-5));
        byte[] der = builder.Build(ca, BigInteger.One, now + validFor, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, thisUpdate: now.AddMinutes(-1));
        return PemEncoding.WriteString("X509 CRL", der);
    }

    [Fact]
    public void A_revoked_certificate_is_refused_by_the_crl_and_the_reason_names_it()
    {
        X509Certificate2 ca = CreateCa("CN=Zabbix CA", out RSA key);
        using (key)
        {
            Issued leaf = Issue("revoked", ca, ServerName, serverAuth: true);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ZabbixRevocationLists lists = ZabbixRevocationLists.Parse(Crl(ca, now, TimeSpan.FromDays(1), leaf.Leaf), "test");
            using X509Chain chain = Chain(ca, leaf.Leaf);

            Assert.False(lists.Check(chain, now, out string? reason));
            Assert.Contains("is revoked", reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_certificate_not_on_a_current_crl_of_every_issuer_passes()
    {
        X509Certificate2 ca = CreateCa("CN=Zabbix CA", out RSA key);
        using (key)
        {
            Issued leaf = Issue("good", ca, ServerName, serverAuth: true);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ZabbixRevocationLists lists = ZabbixRevocationLists.Parse(Crl(ca, now, TimeSpan.FromDays(1)), "test");
            using X509Chain chain = Chain(ca, leaf.Leaf);

            Assert.True(lists.Check(chain, now, out _));
        }
    }

    [Fact]
    public void An_expired_crl_refuses_the_chain()
    {
        X509Certificate2 ca = CreateCa("CN=Zabbix CA", out RSA key);
        using (key)
        {
            Issued leaf = Issue("expired", ca, ServerName, serverAuth: true);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ZabbixRevocationLists lists = ZabbixRevocationLists.Parse(Crl(ca, now, TimeSpan.FromHours(1)), "test");
            using X509Chain chain = Chain(ca, leaf.Leaf);

            Assert.False(lists.Check(chain, now.AddHours(2), out string? reason));
            Assert.Contains("expired", reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_crl_with_the_issuers_name_but_not_its_signature_covers_nothing()
    {
        X509Certificate2 ca = CreateCa("CN=Zabbix CA", out RSA key);
        using X509Certificate2 impostor = CreateCa("CN=Zabbix CA", out RSA impostorKey);
        using (key)
        using (impostorKey)
        {
            Issued leaf = Issue("impostor", ca, ServerName, serverAuth: true);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ZabbixRevocationLists lists = ZabbixRevocationLists.Parse(Crl(impostor, now, TimeSpan.FromDays(1)), "test");
            using X509Chain chain = Chain(ca, leaf.Leaf);

            Assert.False(lists.Check(chain, now, out string? reason));
            Assert.Contains("no CRL signed by", reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_revoked_peer_is_refused_in_the_handshake_when_a_crl_file_is_configured()
    {
        (ZabbixTlsOptions server, Issued serverIssued, Issued client) = Pki();
        string crlFile = Path.Combine(_directory, "ca.crl");
        await File.WriteAllTextAsync(crlFile, Crl(serverIssued.Ca, DateTimeOffset.UtcNow, TimeSpan.FromDays(1), serverIssued.Leaf), TestContext.Current.CancellationToken);
        (ZabbixServerNode node, Task served) = Serve(server);
        ZabbixTlsOptions words = Client(client);
        words.CrlFile = crlFile;
        var logs = new FakeLogCollector();

        await Assert.ThrowsAsync<AuthenticationException>(async () =>
            await ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Tls = words }, logger: new FakeLogger(logs))("hello"u8.ToArray(), TestContext.Current.CancellationToken));

        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1103 && record.Message.Contains("is revoked", StringComparison.Ordinal));
        await served;
    }

    // The listener's certificate side: its TLSCRLFile reaches the handshake, the CRL is judged on the listener's own clock,
    // and the reason a peer is refused is logged by the listener (1103), not only SslStream's generic refusal.
    [Theory]
    [InlineData(true, 0, "is revoked")]
    [InlineData(false, 2, "expired")]
    public async Task A_cert_listener_checks_its_crl_file_on_its_own_clock_and_logs_why_it_refused(bool revoke, int daysLater, string reason)
    {
        (ZabbixTlsOptions server, Issued serverIssued, Issued client) = Pki();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string crlFile = Path.Combine(_directory, "listener.crl");
        await File.WriteAllTextAsync(crlFile, Crl(serverIssued.Ca, now, TimeSpan.FromDays(1), revoke ? [client.Leaf] : []), TestContext.Current.CancellationToken);
        var options = new Zabbix.ZabbixListenerOptions { Address = "127.0.0.1", Port = 0, TlsAccept = ZabbixTlsAcceptModes.Certificate };
        options.AllowedPeers.Add("127.0.0.1");
        options.Tls = new ZabbixTlsOptions { CaFile = server.CaFile, CertificateFile = server.CertificateFile, KeyFile = server.KeyFile, CrlFile = crlFile };
        var logs = new FakeLogCollector();
        var listener = new Zabbix.Listening.ZabbixListener(options, (request, _) => ValueTask.FromResult(request.Payload), ListenerTests.Transport(), new FakeTimeProvider(now.AddDays(daysLater)), new FakeLogger<Zabbix.Listening.ZabbixListener>(logs), meters: null);
        await listener.StartingAsync(TestContext.Current.CancellationToken);
        await listener.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var node = new ZabbixServerNode("127.0.0.1", ((IPEndPoint)listener.LocalEndPoint!).Port);
            await Assert.ThrowsAnyAsync<Exception>(async () => await ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Tls = Client(client) })("hello"u8.ToArray(), TestContext.Current.CancellationToken));
        }
        finally
        {
            await listener.StoppingAsync(TestContext.Current.CancellationToken);
            await listener.StopAsync(TestContext.Current.CancellationToken);
            await listener.StoppedAsync(TestContext.Current.CancellationToken);
            await listener.DisposeAsync();
        }

        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1103 && record.Message.Contains(reason, StringComparison.Ordinal));
    }

    // ----- pre-shared key: the deadline reaches a synchronous handshake ---------------------------------------

    private const string Psk = "1f87b595725ac58dd977beef14b97461a7c1045b9a1c963065002c5473194952";

    [Fact]
    public async Task A_psk_peer_that_stalls_the_handshake_fails_at_the_deadline()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var node = new ZabbixServerNode("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        var hello = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        Task stalling = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            _ = await client.GetStream().ReadAsync(new byte[1]);                             // the ClientHello began; never answer it
            hello.SetResult();
            await release.Task;
        }, TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider();
        ZabbixExchangeCallback exchange = ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Timeout = TimeSpan.FromSeconds(3), Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = "edge-01", Psk = Psk } }, time);

        ValueTask<ReadOnlyMemory<byte>> pending = exchange("x"u8.ToArray(), TestContext.Current.CancellationToken);
        await hello.Task;
        time.Advance(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<TimeoutException>(async () => await pending);
        release.SetResult();
        await stalling;
        listener.Stop();
    }

    [Fact]
    public async Task A_psk_peer_that_stalls_after_the_handshake_fails_at_the_deadline()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var node = new ZabbixServerNode("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        var request = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        Task stalling = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            var server = new TlsServerProtocol(client.GetStream());
            server.Accept(new OneKeyServer(Encoding.UTF8.GetBytes("edge-01"), Convert.FromHexString(Psk)));
            await ZabbixHeader.ReadAsync(server.Stream);                                    // the request arrived; never answer it
            request.SetResult();
            await release.Task;
        }, TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider();
        ZabbixExchangeCallback exchange = ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Timeout = TimeSpan.FromSeconds(3), Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = "edge-01", Psk = Psk } }, time);

        ValueTask<ReadOnlyMemory<byte>> pending = exchange("x"u8.ToArray(), TestContext.Current.CancellationToken);
        await request.Task;
        time.Advance(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<TimeoutException>(async () => await pending);
        release.SetResult();
        await stalling;
        listener.Stop();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_client_s_decoded_psk_is_the_array_handed_to_the_handshake_and_is_zeroed_when_it_ends(bool rightKey)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task serving = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            var server = new TlsServerProtocol(accepted.GetStream());
            try
            {
                server.Accept(new OneKeyServer(Encoding.UTF8.GetBytes("edge-01"), Convert.FromHexString(Psk)));
                ReadOnlyMemory<byte> request = await ZabbixHeader.ReadAsync(server.Stream);
                await ZabbixHeader.WriteAsync(server.Stream, request);
            }
            catch (Exception refused) when (refused is TlsException or IOException) { }
        }, TestContext.Current.CancellationToken);
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, TestContext.Current.CancellationToken);
        byte[]? handed = null;
        var words = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = "edge-01", Psk = rightKey ? Psk : new string('a', 64) };

        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            await using Stream secured = await ZabbixTls.ConnectPskAsync(client.GetStream(), "127.0.0.1", words, NullLogger.Instance,
                hex => handed = Convert.FromHexString(hex), TestContext.Current.CancellationToken);
            await ZabbixHeader.WriteAsync(secured, "ping"u8.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("ping", Encoding.UTF8.GetString((await ZabbixHeader.ReadAsync(secured, cancellationToken: TestContext.Current.CancellationToken)).Span));
        });

        Assert.Equal(rightKey, failure is null);
        if (!rightKey) Assert.IsType<AuthenticationException>(failure);
        Assert.NotNull(handed);
        Assert.All(handed, b => Assert.Equal(0, b));                                          // the decoded key, not a copy of it
        await serving;
    }

    [Fact]
    public async Task Certificate_mode_reads_its_files_once_when_the_exchange_is_built_and_never_per_connection()
    {
        (ZabbixTlsOptions server, _, Issued client) = Pki();
        string copy = Path.Combine(_directory, "once");
        Directory.CreateDirectory(copy);
        ZabbixTlsOptions words = Client(client);
        foreach (string file in new[] { words.CaFile!, words.CertificateFile!, words.KeyFile! }) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        words.CaFile = Path.Combine(copy, Path.GetFileName(words.CaFile!));
        words.CertificateFile = Path.Combine(copy, Path.GetFileName(words.CertificateFile!));
        words.KeyFile = Path.Combine(copy, Path.GetFileName(words.KeyFile!));
        (ZabbixServerNode node, Task served) = Serve(server);
        ZabbixExchangeCallback connected = ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Tls = words });
        Directory.Delete(copy, recursive: true);                                              // gone before the first connection

        ReadOnlyMemory<byte> echo = await connected("hello"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal("hello", Encoding.UTF8.GetString(echo.Span));
        await served;
    }

    [Fact]
    public void A_certificate_file_that_is_missing_fails_when_the_exchange_is_built()
    {
        var words = new ZabbixTlsOptions { Mode = ZabbixTlsMode.Certificate, CaFile = Path.Combine(_directory, "none-ca.pem"), CertificateFile = "c.pem", KeyFile = "c.key" };

        Assert.ThrowsAny<IOException>(() => ZabbixFrames.Over([new ZabbixServerNode("127.0.0.1")], new ZabbixConnectionOptions { Tls = words }));
    }

    /// <summary>A plain BouncyCastle PSK server (TLS 1.2, one identity): the peer a blocking client must not hang on.</summary>
    private sealed class OneKeyServer(byte[] identity, byte[] key) : PskTlsServer(new BcTlsCrypto(new SecureRandom()), new OneIdentity(identity, key))
    {
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv12];

        private sealed class OneIdentity(byte[] identity, byte[] key) : TlsPskIdentityManager
        {
            public byte[]? GetHint() => null;

            public byte[]? GetPsk(byte[] offered) => offered.AsSpan().SequenceEqual(identity) ? key : null;
        }
    }
}
