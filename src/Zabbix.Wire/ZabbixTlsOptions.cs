// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Community.Zabbix;

/// <summary>
/// Represents a connection's TLS settings, one shape for every framed protocol: the mode, the certificate files for
/// <see cref="ZabbixTlsMode.Certificate"/>, the identity and key for <see cref="ZabbixTlsMode.PreSharedKey"/>. The key is
/// a secret and is redacted by classification. Validated as upstream validates its configuration
/// (<c>zbx_tls_validate_config</c>, <c>src/libs/zbxcomms/tls_validate.c</c>): certificate mode needs the CA, certificate
/// and key files together (the CRL file and the pins only beside them); pre-shared-key mode needs the identity and the key.
/// </summary>
/// <remarks>
/// As upstream, certificate mode does not check the peer's host name, and checks revocation only against
/// <see cref="CrlFile"/>; set <see cref="PeerCertificateIssuer"/> and <see cref="PeerCertificateSubject"/> to tell peers
/// signed by one CA apart. The files are read once, when the connection's owner is built. SECURITY.md says what the wire's
/// TLS checks and what it does not.
/// </remarks>
public sealed class ZabbixTlsOptions : IValidatableObject
{
    // Upstream's HOST_TLS_PSK_IDENTITY_LEN (include/zbxcommon.h, 7.0/7.4/8.0 alike; tls.c zbx_check_psk_identity_len).
    private const int MaxPskIdentityBytes = 512;

    /// <summary>The mode.</summary>
    public ZabbixTlsMode Mode { get; set; } = ZabbixTlsMode.Unencrypted;

    /// <summary>This end's certificate, PEM (<c>TLSCertFile</c>).</summary>
    public string? CertificateFile { get; set; }

    /// <summary>This end's private key, PEM (<c>TLSKeyFile</c>).</summary>
    public string? KeyFile { get; set; }

    /// <summary>
    /// The CA bundle that signs the peer's certificate, PEM (<c>TLSCAFile</c>). Required in certificate mode, and the
    /// only trust anchor: the operating system's store is never consulted, as upstream trusts only this file.
    /// </summary>
    public string? CaFile { get; set; }

    /// <summary>
    /// Certificate revocation lists, PEM (<c>TLSCRLFile</c>). When set, every certificate in the peer's chain below the
    /// CA must be covered by a current list from its issuer and not be on it; when unset, revocation is not checked,
    /// as upstream checks only against this file.
    /// </summary>
    public string? CrlFile { get; set; }

    /// <summary>The issuer the peer's certificate must carry, RFC 2253 as OpenSSL prints it (<c>TLSServerCertIssuer</c>); unpinned when unset.</summary>
    public string? PeerCertificateIssuer { get; set; }

    /// <summary>The subject the peer's certificate must carry, RFC 2253 as OpenSSL prints it (<c>TLSServerCertSubject</c>); unpinned when unset.</summary>
    public string? PeerCertificateSubject { get; set; }

    /// <summary>The PSK identity (<c>TLSPSKIdentity</c>), at most 512 bytes of UTF-8.</summary>
    public string? PskIdentity { get; set; }

    /// <summary>The pre-shared key, hex, 32 to 512 characters (<c>TLSPSKFile</c>'s contents).</summary>
    [ZabbixSecret]
    public string? Psk { get; set; }

    /// <summary>A copy that changes to these options don't reach.</summary>
    /// <returns>The copy.</returns>
    internal ZabbixTlsOptions Clone() => new()
    {
        Mode = Mode,
        CertificateFile = CertificateFile,
        KeyFile = KeyFile,
        CaFile = CaFile,
        CrlFile = CrlFile,
        PeerCertificateIssuer = PeerCertificateIssuer,
        PeerCertificateSubject = PeerCertificateSubject,
        PskIdentity = PskIdentity,
        Psk = Psk,
    };

    /// <inheritdoc/>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        switch (Mode)
        {
            case ZabbixTlsMode.Certificate:
                if (string.IsNullOrWhiteSpace(CaFile))
                    yield return new ValidationResult("certificate mode needs CaFile: it is the only trust anchor", [nameof(CaFile)]);
                if (string.IsNullOrWhiteSpace(CertificateFile) || string.IsNullOrWhiteSpace(KeyFile))
                    yield return new ValidationResult("certificate mode needs CertificateFile and KeyFile: upstream always presents this end's certificate", [nameof(CertificateFile), nameof(KeyFile)]);
                break;
            case ZabbixTlsMode.PreSharedKey:
                if (CrlFile is not null || PeerCertificateIssuer is not null || PeerCertificateSubject is not null)
                    yield return new ValidationResult("CrlFile and the peer certificate pins belong to certificate mode", [nameof(CrlFile)]);
                if (string.IsNullOrEmpty(PskIdentity))
                    yield return new ValidationResult("pre-shared-key mode needs a PskIdentity", [nameof(PskIdentity)]);
                else if (Encoding.UTF8.GetByteCount(PskIdentity) > MaxPskIdentityBytes)
                    yield return new ValidationResult($"a PSK identity is at most {MaxPskIdentityBytes} bytes of UTF-8, as upstream reads it", [nameof(PskIdentity)]);
                if (string.IsNullOrEmpty(Psk))
                    yield return new ValidationResult("pre-shared-key mode needs a Psk", [nameof(Psk)]);
                // Checked here, not first at the handshake: a malformed key that reached the send loop would fail every
                // exchange with a FormatException, which the loop does not retry as a transport failure.
                else if (!IsPsk(Psk))
                    yield return new ValidationResult("the Psk is not an even number of hex digits, 32 to 512", [nameof(Psk)]);
                break;
        }
    }

    private static bool IsPsk(string hex)
    {
        try
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(Protocol.ZabbixTls.ParsePsk(hex).Span);
            return true;
        }
        catch (Exception failure) when (failure is FormatException or ArgumentException)
        {
            return false;
        }
    }
}
