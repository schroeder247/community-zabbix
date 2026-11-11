// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Protocol;

/// <summary>
/// Represents what certificate mode trusts and presents, read once from a <see cref="ZabbixTlsOptions"/>' files: the CA
/// file as the only trust anchor, this end's certificate and key with the chain the certificate file carries after the
/// leaf, the revocation lists of <see cref="ZabbixTlsOptions.CrlFile"/> and the issuer and subject pins. Upstream reads
/// the same files once, when a process starts (<c>zbx_tls_init_child</c>), so a connection never re-reads or re-verifies
/// them; one instance serves every connection of its owner, and disposing it releases the private key.
/// </summary>
internal sealed class ZabbixCertificateTrust : IDisposable
{
    private readonly X509ChainPolicy _policy;
    private readonly X509Certificate2 _identity;
    private readonly X509Certificate2Collection _anchors;
    private readonly X509Certificate2Collection _chain;
    private readonly ZabbixRevocationLists? _revocation;
    private readonly string? _issuer;
    private readonly string? _subject;
    private readonly TimeProvider _time;

    private ZabbixCertificateTrust(ZabbixTlsOptions options, string caFile, string certificateFile, string keyFile, TimeProvider time)
    {
        _anchors = [];
        _chain = [];
        try
        {
            _anchors.ImportFromPemFile(caFile);
            _policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,                              // the CA file, never the OS store
                RevocationMode = X509RevocationMode.NoCheck,                                  // only TLSCRLFile, checked in the callback
            };
            _policy.CustomTrustStore.AddRange(_anchors);

            // The certificate file may carry its chain after the leaf, as OpenSSL's use_certificate_chain_file reads it.
            _chain.ImportFromPemFile(certificateFile);
            _identity = LoadIdentity(certificateFile, keyFile);
            Identity = SslStreamCertificateContext.Create(_identity, [.. _chain.Skip(1)], offline: true);
            _revocation = options.CrlFile is { } crl ? ZabbixRevocationLists.Load(crl) : null;
        }
        catch
        {
            Release();
            throw;
        }
        _issuer = options.PeerCertificateIssuer;
        _subject = options.PeerCertificateSubject;
        _time = time;
    }

    // A key read from PEM is ephemeral, and SChannel refuses an ephemeral key ("the credentials supplied to the package
    // were not recognized"). On Windows the identity goes through PKCS#12, as Kestrel's certificate loader does: the key
    // is stored in the account's key store until the certificate is disposed.
    private static X509Certificate2 LoadIdentity(string certificateFile, string keyFile)
    {
        X509Certificate2 identity = X509Certificate2.CreateFromPemFile(certificateFile, keyFile);
        if (!OperatingSystem.IsWindows()) return identity;
        using (identity)
        {
            byte[] pkcs12 = identity.Export(X509ContentType.Pkcs12);
            try
            {
                return X509CertificateLoader.LoadPkcs12(pkcs12, null);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs12);
            }
        }
    }

    /// <summary>Gets what this end presents: its certificate, its key and its chain.</summary>
    public SslStreamCertificateContext Identity { get; }

    /// <summary>Gets a fresh copy of the chain policy for one connection: the CA file as the only anchor, no online revocation.</summary>
    public X509ChainPolicy Policy => _policy.Clone();

    /// <summary>Reads the files <paramref name="options"/> names.</summary>
    /// <param name="options">Certificate-mode options that validate.</param>
    /// <param name="time">The clock a revocation list's validity is judged on.</param>
    /// <returns>The trust, owned by the caller.</returns>
    /// <exception cref="ArgumentException"><paramref name="options"/> does not name the CA, certificate and key files.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">A file is not PEM, or the key does not match the certificate.</exception>
    /// <exception cref="InvalidDataException">The CRL file holds no list, or a list is not RFC 5280.</exception>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public static ZabbixCertificateTrust Load(ZabbixTlsOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        string caFile = options.CaFile ?? throw new ArgumentException("Certificate mode needs CaFile.", nameof(options));
        string certificateFile = options.CertificateFile ?? throw new ArgumentException("Certificate mode needs CertificateFile.", nameof(options));
        string keyFile = options.KeyFile ?? throw new ArgumentException("Certificate mode needs KeyFile.", nameof(options));
        return new ZabbixCertificateTrust(options, caFile, certificateFile, keyFile, time);
    }

    /// <summary>The callback that holds the peer's chain to the pins and the revocation lists, logging a refusal (1103).</summary>
    /// <param name="peer">What the peer is called in the log: the host connected to, or the accepted endpoint.</param>
    /// <param name="logger">Where a refusal's reason is logged.</param>
    /// <returns>A callback for <see cref="SslStream"/>.</returns>
    public RemoteCertificateValidationCallback Callback(string peer, ILogger logger) => (_, certificate, chain, errors) =>
    {
        string? reason = Refusal(certificate, chain, errors);
        if (reason is null) return true;
        WireLog.TlsPeerRefused(logger, peer, reason);
        return false;
    };

    /// <summary>Gets whether every certificate this trust read has released its handle (its key included).</summary>
    internal bool IsReleased
        => _identity.Handle == IntPtr.Zero && _anchors.All(certificate => certificate.Handle == IntPtr.Zero) && _chain.All(certificate => certificate.Handle == IntPtr.Zero);

    /// <inheritdoc/>
    public void Dispose() => Release();

    private void Release()
    {
        _identity?.Dispose();
        foreach (X509Certificate2 certificate in _anchors) certificate.Dispose();
        foreach (X509Certificate2 certificate in _chain) certificate.Dispose();
    }

    private string? Refusal(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null || chain is null || chain.ChainElements.Count == 0) return "the peer presented no certificate";
        // Upstream never checks the host name in certificate mode (tls_openssl.c sets no host to verify); the pins do.
        errors &= ~SslPolicyErrors.RemoteCertificateNameMismatch;
        if (errors != SslPolicyErrors.None)
            return $"{errors}: {string.Join(", ", chain.ChainStatus.Select(status => status.Status))} (the CA file is the only trust anchor)";
        if (_revocation is not null && !_revocation.Check(chain, _time.GetUtcNow(), out string? revoked)) return revoked;
        X509Certificate2 leaf = chain.ChainElements[0].Certificate;
        return Mismatch("issuer", _issuer, leaf.IssuerName) ?? Mismatch("subject", _subject, leaf.SubjectName);
    }

    // tls_openssl.c: `strcmp(tls_issuer, issuer)` against the name printed RFC 2253 — ordinal, case-sensitive.
    private static string? Mismatch(string what, string? pin, X500DistinguishedName name)
    {
        if (string.IsNullOrEmpty(pin)) return null;
        string actual = ZabbixCertificateName.Format(name);
        return string.Equals(actual, pin, StringComparison.Ordinal) ? null : $"{what} \"{actual}\" is not the pinned \"{pin}\"";
    }
}
