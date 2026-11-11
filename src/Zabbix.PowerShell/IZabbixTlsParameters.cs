// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Security;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// The TLS words a wire command takes, as Zabbix's agent and proxy configuration names them: the pre-shared key and its
/// identity, this end's certificate and key, the CA, the CRL, and the pins on the peer's certificate. The connecting
/// commands add the mode (<c>-Tls</c>, Zabbix's <c>TLSConnect</c>), the listeners the modes they accept
/// (<c>-TlsAccept</c>); <see cref="ZabbixTlsParameters"/> builds the SDK's <see cref="ZabbixTlsOptions"/> from either.
/// </summary>
internal interface IZabbixTlsParameters
{
    /// <summary>The pre-shared-key identity (<c>TLSPSKIdentity</c>).</summary>
    string? PskIdentity { get; }

    /// <summary>The pre-shared key (<c>TLSPSKFile</c>'s contents).</summary>
    SecureString? Psk { get; }

    /// <summary>This end's certificate file (<c>TLSCertFile</c>).</summary>
    string? Certificate { get; }

    /// <summary>This end's private key file (<c>TLSKeyFile</c>).</summary>
    string? CertificateKey { get; }

    /// <summary>The CA file (<c>TLSCAFile</c>).</summary>
    string? CertificateAuthority { get; }

    /// <summary>The CRL file (<c>TLSCRLFile</c>).</summary>
    string? CertificateRevocationList { get; }

    /// <summary>The issuer the peer's certificate must carry (<c>TLSServerCertIssuer</c>).</summary>
    string? ServerIssuer { get; }

    /// <summary>The subject the peer's certificate must carry (<c>TLSServerCertSubject</c>).</summary>
    string? ServerSubject { get; }
}
