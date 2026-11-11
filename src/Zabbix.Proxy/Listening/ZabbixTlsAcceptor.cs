// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Community.Zabbix.Listening;

/// <summary>
/// Enforces <c>TLSAccept</c> as a set on each accepted connection, as upstream's <c>zbx_tcp_accept</c>
/// (<c>comms.c</c>) and <c>zbx_tls_accept</c> (<c>tls_openssl.c</c>) do.
/// </summary>
/// <remarks>
/// <para>A first byte of <c>0x16</c> is a TLS handshake record; anything else is a plain Zabbix frame. A hello offering
/// a PSK key exchange goes to the PSK server here, any other to the certificate server
/// (<see cref="ZabbixTls.WrapServerAsync"/>); a kind outside the set is refused with upstream's words. The hello is read
/// here because <see cref="System.Net.Security.SslStream"/> has no PSK and its <c>ServerOptionsSelectionCallback</c> never
/// sees the offered suites, while one BouncyCastle server for both would move certificate trust off
/// <see cref="System.Security.Cryptography.X509Certificates.X509Chain"/>; so the ClientHello is peeked (nothing consumed)
/// and classified by <see cref="TlsUtilities.GetKeyExchangeAlgorithm(int)"/>.</para>
/// <para>The PSK server runs BouncyCastle non-blocking (<see cref="TlsServerProtocol()"/>) over the connection's pipes,
/// so the deadline cancels a stalled handshake; it sends no identity hint, looks the identity up in
/// <see cref="ZabbixListenerOptions.PskTable"/>, and zeroes the key with <see cref="CryptographicOperations.ZeroMemory"/>.</para>
/// </remarks>
internal sealed class ZabbixTlsAcceptor : IDisposable
{
    /// <summary>
    /// The suites offered: upstream's <c>kECDHEPSK+AES128:kPSK+AES128</c> as OpenSSL 3.6 expands it, the one TLS 1.2 list
    /// the client side offers too (<see cref="ZabbixTls.s_pskCipherSuites"/>, where the list and its measurement live).
    /// </summary>
    internal static readonly ImmutableArray<int> s_cipherSuites = ZabbixTls.s_pskCipherSuites;

    private const byte HandshakeRecord = 0x16;
    private const byte ClientHello = 0x01;

    private readonly ZabbixTlsAcceptModes _accept;
    private readonly ZabbixCertificateTrust? _certificate;
    private readonly (byte[] Identity, string Key)[] _psk;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    /// <summary>An acceptor for <paramref name="options"/>' accept set.</summary>
    /// <param name="options">The listener's options.</param>
    /// <param name="logger">The listener's logger: a refused certificate's reason is logged there.</param>
    /// <param name="time">The listener's clock: a CRL's validity is judged on it.</param>
    public ZabbixTlsAcceptor(ZabbixListenerOptions options, ILogger logger, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);
        _logger = logger;
        _time = time;
        _accept = options.TlsAccept;
        if (_accept.HasFlag(ZabbixTlsAcceptModes.Certificate) && options.Tls is { } tls)
        {
            _certificate = ZabbixCertificateTrust.Load(new ZabbixTlsOptions
            {
                Mode = ZabbixTlsMode.Certificate,
                CertificateFile = tls.CertificateFile,
                KeyFile = tls.KeyFile,
                CaFile = tls.CaFile,
                CrlFile = tls.CrlFile,
                PeerCertificateIssuer = tls.PeerCertificateIssuer,
                PeerCertificateSubject = tls.PeerCertificateSubject,
            }, time);
        }
        _psk = [.. options.PskTable().Select(entry => (Encoding.UTF8.GetBytes(entry.Key), entry.Value))];
    }

    /// <summary>What certificate mode trusts and presents; <see langword="null"/> when the listener does not accept certificates.</summary>
    internal ZabbixCertificateTrust? Certificate => _certificate;

    /// <summary>Releases the certificate and key the acceptor read; a connection already accepted keeps its stream.</summary>
    public void Dispose() => _certificate?.Dispose();

    /// <summary>
    /// Admits the connection's protection: the pipes to read the frame from and write the reply to, the kind it arrived
    /// with, and what to dispose afterwards. Throws <see cref="ZabbixConnectionRefusedException"/> for a kind outside
    /// the set, <see cref="AuthenticationException"/> for a failed handshake.
    /// </summary>
    public async ValueTask<ZabbixAcceptedConnection> AcceptAsync(IDuplexPipe transport, string peer, CancellationToken cancellationToken)
    {
        ReadResult first = await transport.Input.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (first.Buffer.IsEmpty)
        {
            transport.Input.AdvanceTo(first.Buffer.Start);
            throw new EndOfStreamException("The peer closed before sending anything.");
        }
        byte kind = first.Buffer.FirstSpan[0];
        transport.Input.AdvanceTo(first.Buffer.Start);                                    // peeked, not consumed

        if (kind != HandshakeRecord)
        {
            if (!_accept.HasFlag(ZabbixTlsAcceptModes.Unencrypted))
                throw new ZabbixConnectionRefusedException("Unencrypted connections are not allowed.", ZabbixListenerRejection.UnencryptedNotAllowed);
            return new ZabbixAcceptedConnection(transport, ZabbixTlsAcceptModes.Unencrypted, null);
        }
        const ZabbixTlsAcceptModes tlsKinds = ZabbixTlsAcceptModes.Psk | ZabbixTlsAcceptModes.Certificate;
        ZabbixTlsAcceptModes allowed = _accept & tlsKinds;
        if (allowed == 0) throw new ZabbixConnectionRefusedException("TLS connections are not allowed.", ZabbixListenerRejection.TlsNotAllowed);
        bool psk = allowed == tlsKinds ? await OffersPskAsync(transport.Input, cancellationToken).ConfigureAwait(false) : allowed == ZabbixTlsAcceptModes.Psk;
        return psk ? await AcceptPskAsync(transport, cancellationToken).ConfigureAwait(false) : await AcceptCertificateAsync(transport, peer, cancellationToken).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "On success the SslStream owns the adapter (leaveInnerStreamOpen: false); on failure it is disposed here.")]
    private async ValueTask<ZabbixAcceptedConnection> AcceptCertificateAsync(IDuplexPipe transport, string peer, CancellationToken cancellationToken)
    {
        var raw = new DuplexPipeStream(transport);
        try
        {
            ZabbixCertificateTrust trust = _certificate ?? throw new AuthenticationException("The listener accepts certificates but has no certificate to present.");
            Stream secured = await ZabbixTls.WrapServerAsync(raw, trust, peer, _logger, cancellationToken).ConfigureAwait(false);
            return new ZabbixAcceptedConnection(StreamPipe(secured), ZabbixTlsAcceptModes.Certificate, secured);
        }
        catch
        {
            await raw.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<ZabbixAcceptedConnection> AcceptPskAsync(IDuplexPipe transport, CancellationToken cancellationToken)
    {
        var identities = new PskIdentities(_psk);
        var protocol = new TlsServerProtocol();                                            // non-blocking: no stream; this code moves the bytes between the pipes and the protocol
        try
        {
            protocol.Accept(new PskServer(new BcTlsCrypto(new SecureRandom()), identities));
            while (true)
            {
                await ZabbixPskStream.DrainAsync(protocol, transport.Output, cancellationToken).ConfigureAwait(false);
                if (!protocol.IsHandshaking) break;
                ReadResult read = await transport.Input.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (read.Buffer.IsEmpty && read.IsCompleted) throw new EndOfStreamException("The peer closed during the TLS handshake.");
                ZabbixPskStream.Offer(protocol, read.Buffer);
                transport.Input.AdvanceTo(read.Buffer.End);
            }
        }
        catch (TlsException failure)
        {
            // BouncyCastle queued the alert (unknown_psk_identity, …): send it, as OpenSSL does, so the peer's log says why.
            try { await ZabbixPskStream.DrainAsync(protocol, transport.Output, cancellationToken).ConfigureAwait(false); }
            catch (Exception unsent) when (unsent is IOException or InvalidOperationException or OperationCanceledException) { /* the peer is gone */ }
            throw new AuthenticationException($"The PSK handshake failed: {failure.Message}", failure);
        }
        finally
        {
            identities.Zero();
        }
        var stream = new ZabbixPskStream(protocol, transport);
        return new ZabbixAcceptedConnection(StreamPipe(stream), ZabbixTlsAcceptModes.Psk, stream);
    }

    private static DuplexPipe StreamPipe(Stream stream)
        => new(PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true)), PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true)));

    // Reads (without consuming) the first TLS record and says whether its ClientHello offers a PSK key exchange.
    private static async ValueTask<bool> OffersPskAsync(PipeReader input, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult read = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = read.Buffer;
            if (buffer.Length >= 5)
            {
                long record = 5 + RecordLength(buffer);
                if (buffer.Length >= record)
                {
                    bool psk = ClientHelloOffersPsk(buffer.Slice(5, record - 5).ToArray());
                    input.AdvanceTo(buffer.Start);                                           // the handshake reads it again
                    return psk;
                }
            }
            if (read.IsCompleted)
            {
                input.AdvanceTo(buffer.Start);
                throw new EndOfStreamException("The peer closed inside its TLS hello.");
            }
            input.AdvanceTo(buffer.Start, buffer.End);                                      // examined all of it: wait for more
        }
    }

    private static int RecordLength(ReadOnlySequence<byte> buffer)
    {
        Span<byte> header = stackalloc byte[5];
        buffer.Slice(0, 5).CopyTo(header);
        return BinaryPrimitives.ReadUInt16BigEndian(header[3..]);
    }

    // ClientHello (RFC 5246 §7.4.1.2): type, length(3), version(2), random(32), session_id<0..32>, cipher_suites<2..2^16-2>.
    private static bool ClientHelloOffersPsk(ReadOnlySpan<byte> handshake)
    {
        if (handshake.Length < 4 + 2 + 32 + 1 || handshake[0] != ClientHello) return false;
        int at = 4 + 2 + 32;
        at += 1 + handshake[at];
        if (handshake.Length < at + 2) return false;
        int length = BinaryPrimitives.ReadUInt16BigEndian(handshake[at..]);
        ReadOnlySpan<byte> suites = handshake.Slice(at + 2, Math.Min(length, handshake.Length - at - 2));
        for (int i = 0; i + 1 < suites.Length; i += 2)
        {
            int kx = TlsUtilities.GetKeyExchangeAlgorithm(BinaryPrimitives.ReadUInt16BigEndian(suites[i..]));
            if (kx is KeyExchangeAlgorithm.PSK or KeyExchangeAlgorithm.ECDHE_PSK or KeyExchangeAlgorithm.DHE_PSK or KeyExchangeAlgorithm.RSA_PSK) return true;
        }
        return false;
    }

    private sealed class PskServer(TlsCrypto crypto, TlsPskIdentityManager identities) : PskTlsServer(crypto, identities)
    {
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv12];

        // BouncyCastle's PskTlsServer owns the array it is handed; a fresh copy off the immutable list keeps that array ours.
        protected override int[] GetSupportedCipherSuites() => [.. s_cipherSuites];
    }

    /// <summary>The listener's identities for one handshake; the key handed out is zeroed when the handshake ends.</summary>
    internal sealed class PskIdentities((byte[] Identity, string Key)[] table, Func<string, byte[]>? parse = null) : TlsPskIdentityManager
    {
        private readonly Func<string, byte[]> _parse = parse ?? ParseKey;
        private byte[]? _handedOut;

        // The one array the hex is decoded into is the one handed to BouncyCastle and the one zeroed: a copy here would
        // leave the decoded key in the heap until the collector reuses it.
        private static byte[] ParseKey(string hex)
            => System.Runtime.InteropServices.MemoryMarshal.TryGetArray<byte>(ZabbixTls.ParsePsk(hex), out ArraySegment<byte> decoded)
               && decoded is { Array: { } array, Offset: 0 } && decoded.Count == array.Length
                ? array
                : throw new System.Diagnostics.UnreachableException("ParsePsk decodes into an array of its own.");

        // Upstream sets no identity hint (no SSL_CTX_use_psk_identity_hint): the hint is not the server's identity.
        public byte[]? GetHint() => null;

        public byte[]? GetPsk(byte[] identity)
        {
            foreach ((byte[] known, string key) in table)
            {
                if (!known.AsSpan().SequenceEqual(identity)) continue;
                Zero();
                return _handedOut = _parse(key);
            }
            return null;                                                                       // BouncyCastle answers unknown_psk_identity
        }

        public void Zero()
        {
            if (_handedOut is { } key) CryptographicOperations.ZeroMemory(key);
            _handedOut = null;
        }
    }
}
