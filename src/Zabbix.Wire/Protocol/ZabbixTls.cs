// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Community.Zabbix.Protocol;

/// <summary>
/// Provides TLS on a framed connection, once for every protocol. Certificate mode is the platform's
/// <see cref="SslStream"/> with Zabbix's trust rules: the configured CA is the only trust anchor
/// (<see cref="X509ChainTrustMode.CustomRootTrust"/> through <see cref="SslClientAuthenticationOptions.CertificateChainPolicy"/>),
/// the host name is not checked (upstream
/// relies on the issuer/subject pins instead), the pins are compared as upstream prints names
/// (<see cref="ZabbixCertificateName"/>, ordinal and case-sensitive), and revocation is checked only against a configured
/// CRL file. Pre-shared-key mode is BouncyCastle's managed TLS (the rejection is at <c>PskClientSuites</c>).
/// </summary>
internal static class ZabbixTls
{
    // Certificates: upstream sets a TLS 1.2 minimum on its certificate context (tls_openssl.c:936, SSL_CTX_set_min_proto_version,
    // 7.0/7.4/8.0 alike). SslProtocols expresses a set, not a minimum, and None hands the choice to the OS, which
    // negotiates TLS 1.0/1.1 where the host still enables them (CA5398's advice assumes an OS policy at least as strict as
    // the peer's contract; Zabbix's contract is the stricter one here). The set upstream's minimum admits today is 1.2 and
    // 1.3; a later version is added here when .NET and Zabbix both speak it.
#pragma warning disable CA5398 // hard-coded on purpose: the minimum is Zabbix's protocol contract, not a host preference
    internal static SslProtocols CertificateProtocols => SslProtocols.Tls12 | SslProtocols.Tls13;
#pragma warning restore CA5398

    // Pre-shared keys: .NET 10.0.11 has no TLS-PSK API (the only PSK names in the shared framework's reference assemblies are
    // the TlsCipherSuite enum's members; SslStream offers no way to supply a key), so BouncyCastle is the one
    // third-party boundary tool. Upstream's lists (tls_openssl.c, 7.0/7.4/8.0 alike): TLS 1.3
    // "TLS_CHACHA20_POLY1305_SHA256:TLS_AES_128_GCM_SHA256", TLS 1.2 "kECDHEPSK+AES128:kPSK+AES128", minimum TLS 1.2.
    // `openssl ciphers -v` on OpenSSL 3.6.4 expands the TLS 1.2 string to ECDHE-PSK-AES128-CBC-SHA256,
    // ECDHE-PSK-AES128-CBC-SHA, PSK-AES128-GCM-SHA256, PSK-AES128-CCM, PSK-AES128-CCM8, PSK-AES128-CBC-SHA256,
    // PSK-AES128-CBC-SHA — OpenSSL has no ECDHE-PSK GCM suite. Against `openssl s_server` configured with
    // upstream's strings, every suite below completes a handshake with BouncyCastle, TLS 1.3 included (external PSK,
    // SHA-256); PSK-AES128-CCM8 is refused by the server ("no shared cipher"), so it is not offered.
    //
    // The TLS 1.2 half, upstream's `kECDHEPSK+AES128:kPSK+AES128` as OpenSSL expands it (above). Internal: the
    // listener's PSK acceptor (Zabbix.Proxy) offers this list itself.
    internal static readonly ImmutableArray<int> s_pskCipherSuites =
    [
        CipherSuite.TLS_ECDHE_PSK_WITH_AES_128_CBC_SHA256,
        CipherSuite.TLS_ECDHE_PSK_WITH_AES_128_CBC_SHA,
        CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256,
        CipherSuite.TLS_PSK_WITH_AES_128_CCM,
        CipherSuite.TLS_PSK_WITH_AES_128_CBC_SHA256,
        CipherSuite.TLS_PSK_WITH_AES_128_CBC_SHA,
    ];

    private static readonly ImmutableArray<int> s_pskClientSuites =
    [
        CipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        CipherSuite.TLS_AES_128_GCM_SHA256,
        .. s_pskCipherSuites,
    ];

    /// <summary>
    /// Wraps a client connection as <paramref name="options"/> says; the stream itself when unencrypted. A refused
    /// handshake is an <see cref="AuthenticationException"/> in both modes, and the reason is logged (1103) with what was
    /// compared against what.
    /// </summary>
    /// <param name="connection">The TCP stream; disposed with the returned stream.</param>
    /// <param name="host">The peer, for SNI and the logs.</param>
    /// <param name="options">The TLS words; unencrypted when <see langword="null"/>.</param>
    /// <param name="trust">Certificate mode's files, read once by the connection's owner; required in that mode.</param>
    /// <param name="logger">Where refusals are logged.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <returns>The stream to exchange frames over.</returns>
    /// <exception cref="ArgumentException"><paramref name="options"/> does not validate, or certificate mode has no <paramref name="trust"/>.</exception>
    /// <exception cref="AuthenticationException">The handshake was refused, by either end.</exception>
    public static async ValueTask<Stream> WrapClientAsync(Stream connection, string host, ZabbixTlsOptions? options, ZabbixCertificateTrust? trust, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);
        if (options is null || options.Mode == ZabbixTlsMode.Unencrypted) return connection;
        Validate(options);
        switch (options.Mode)
        {
            case ZabbixTlsMode.Certificate:
                {
                    ZabbixCertificateTrust certificates = trust ?? throw new ArgumentException("Certificate mode needs the trust its owner loaded from the options.", nameof(trust));
                    var tls = new SslStream(connection, leaveInnerStreamOpen: false, certificates.Callback(host, logger));
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                        ClientCertificateContext = certificates.Identity,
                        CertificateChainPolicy = certificates.Policy,
                        EnabledSslProtocols = CertificateProtocols,
                    }, cancellationToken).ConfigureAwait(false);
                    return tls;
                }

            case ZabbixTlsMode.PreSharedKey:
                return await ConnectPskAsync(connection, host, options, logger, DecodePsk, cancellationToken).ConfigureAwait(false);

            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Mode, "The TLS mode is not one the SDK knows.");
        }
    }

    /// <summary>
    /// Wraps an accepted connection in certificate mode: the peer's certificate is required and held to the same trust
    /// rules as the client side. A pre-shared-key server is the listener's own acceptor (<c>Zabbix.Listening</c>), which
    /// answers per identity.
    /// </summary>
    /// <param name="connection">The accepted stream; disposed with the returned stream.</param>
    /// <param name="trust">The listener's certificate files, read once.</param>
    /// <param name="peer">What the peer is called in the log.</param>
    /// <param name="logger">Where a refused peer's reason is logged (1103): the pin, chain or revocation it failed.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <returns>The stream to exchange frames over.</returns>
    /// <exception cref="AuthenticationException">The handshake was refused, by either end.</exception>
    public static async ValueTask<Stream> WrapServerAsync(Stream connection, ZabbixCertificateTrust trust, string peer, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(logger);
        var tls = new SslStream(connection, leaveInnerStreamOpen: false, trust.Callback(peer, logger));
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificateContext = trust.Identity,
            ClientCertificateRequired = true,
            CertificateChainPolicy = trust.Policy,
            EnabledSslProtocols = CertificateProtocols,
        }, cancellationToken).ConfigureAwait(false);
        return tls;
    }

    /// <summary>
    /// A PSK from its hex form: even length, 32 to 512 characters (128 to 2048 bits, upstream's <c>HOST_TLS_PSK_LEN_MIN</c>
    /// and <c>HOST_TLS_PSK_LEN</c>, <c>include/zbxcommon.h</c>), hex only. The bytes are the caller's own
    /// array (never shared), so <see cref="System.Security.Cryptography.CryptographicOperations.ZeroMemory(Span{byte})"/>
    /// over the returned memory's span actually zeroes what was parsed.
    /// </summary>
    /// <param name="hex">The key as Zabbix stores it.</param>
    /// <returns>The key's bytes, in an array of their own.</returns>
    /// <exception cref="FormatException">The text is not an even number of hex characters from 32 to 512.</exception>
    public static Memory<byte> ParsePsk(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        if ((hex.Length & 1) != 0) throw new FormatException("A PSK has an even number of hex characters.");
        if (hex.Length < 32) throw new FormatException("A PSK is at least 32 hex characters (128 bits).");
        if (hex.Length > 512) throw new FormatException("A PSK is at most 512 hex characters (2048 bits), as upstream reads it.");
        return Convert.FromHexString(hex);
    }

    internal static void Validate(ZabbixTlsOptions options)
    {
        List<ValidationResult> failures = [.. options.Validate(new ValidationContext(options, nameof(ZabbixTlsOptions), null, null))];
        if (failures.Count > 0) throw new ArgumentException(string.Join(" ", failures.Select(failure => failure.ErrorMessage)), nameof(options));
    }

    // The one array the hex is decoded into: handed to BouncyCastle as the key, and zeroed when the handshake ends. A copy
    // here would leave the decoded key in the heap until the collector reuses it.
    private static byte[] DecodePsk(string hex)
        => System.Runtime.InteropServices.MemoryMarshal.TryGetArray<byte>(ParsePsk(hex), out ArraySegment<byte> decoded)
           && decoded is { Array: { } array, Offset: 0 } && decoded.Count == array.Length
            ? array
            : throw new System.Diagnostics.UnreachableException("ParsePsk decodes into an array of its own.");

    /// <summary>
    /// The client's PSK handshake, BouncyCastle in non-blocking mode over the connection (<see cref="ZabbixPskClientStream"/>).
    /// Its refusals (a fatal alert raised or received) surface as the <see cref="AuthenticationException"/>
    /// <see cref="SslStream"/> throws for the same event, so a caller sees one handshake failure. The decoded key and the
    /// secret derived from it are zeroed when the handshake ends, whatever its outcome.
    /// </summary>
    /// <param name="connection">The connected transport; owned by the returned stream.</param>
    /// <param name="host">The peer, for the logs.</param>
    /// <param name="options">Pre-shared-key options that validate.</param>
    /// <param name="logger">Where a refusal is logged (1103).</param>
    /// <param name="decode">Decodes the hex key into the array handed to BouncyCastle (a seam: a test sees the array zeroed).</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <returns>The protected stream.</returns>
    internal static async ValueTask<Stream> ConnectPskAsync(Stream connection, string host, ZabbixTlsOptions options, ILogger logger, Func<string, byte[]> decode, CancellationToken cancellationToken)
    {
        string identity = options.PskIdentity ?? throw new ArgumentException("Pre-shared-key mode needs a PskIdentity.", nameof(options));
        string hex = options.Psk ?? throw new ArgumentException("Pre-shared-key mode needs a Psk.", nameof(options));
        byte[] key = decode(hex);
        var client = new PskClient(new BcTlsCrypto(new SecureRandom()), Encoding.UTF8.GetBytes(identity), key);
        try
        {
            return await ZabbixPskClientStream.ConnectAsync(connection, client, cancellationToken).ConfigureAwait(false);
        }
        catch (TlsException refused) when (!cancellationToken.IsCancellationRequested)
        {
            WireLog.TlsPeerRefused(logger, host, "pre-shared-key handshake: " + refused.Message);
            throw new AuthenticationException("The pre-shared-key handshake was refused: " + refused.Message, refused);
        }
        finally
        {
            client.Zero();
        }
    }

    /// <summary>
    /// The client side: TLS 1.3 with the key as an external PSK (SHA-256, as upstream's TLS 1.3 suites require) and TLS 1.2
    /// with the PSK suites, in upstream's order, so a server that offers only one of them is met there.
    /// </summary>
    private sealed class PskClient(TlsCrypto crypto, byte[] identity, byte[] key) : PskTlsClient(crypto, new PskIdentity(identity, key))
    {
        private readonly List<TlsSecret> _secrets = [];

        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv13, ProtocolVersion.TLSv12];

        // BouncyCastle's PskTlsClient owns the array it is handed; a fresh copy off the immutable list keeps that array ours.
        protected override int[] GetSupportedCipherSuites() => [.. s_pskClientSuites];

        public override IList<TlsPskExternal> GetExternalPsks()
        {
            TlsSecret secret = Crypto.CreateSecret(key);
            lock (_secrets) _secrets.Add(secret);
            return [new BasicTlsPskExternal(identity, secret, PrfAlgorithm.tls13_hkdf_sha256)];
        }

        // Upstream's servers allow one group, P-256 (zbx_set_ecdhe_parameters: SSL_CTX_set1_groups NID_X9_62_prime256v1).
        // BouncyCastle's default first key share is X25519, which makes such a server answer HelloRetryRequest, and
        // BouncyCastle fails a retried hello that carries an external PSK (internal_error, "Not buffering"), as seen
        // live against Zabbix 7.0.31 and the 8.0 release candidate and against `openssl s_server -groups P-256`. So the client offers P-256 only.
        protected override IList<int> GetSupportedGroups(IList<int> namedGroupRoles) => [NamedGroup.secp256r1];

        public override IList<int> GetEarlyKeyShareGroups() => [NamedGroup.secp256r1];

        /// <summary>Zeroes the decoded key and destroys every secret made from it.</summary>
        public void Zero()
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            lock (_secrets)
            {
                foreach (TlsSecret secret in _secrets) secret.Destroy();
                _secrets.Clear();
            }
        }
    }

    /// <summary>
    /// The identity and the key as BouncyCastle asks for them. <see cref="BasicTlsPskIdentity"/> copies the key it is given,
    /// and a copy would outlive the zeroing; this hands out the one decoded array.
    /// </summary>
    private sealed class PskIdentity(byte[] identity, byte[] key) : TlsPskIdentity
    {
        public byte[] GetPskIdentity() => identity;

        public byte[] GetPsk() => key;

        public void NotifyIdentityHint(byte[] psk_identity_hint)
        {
        }

        public void SkipIdentityHint()
        {
        }
    }
}
