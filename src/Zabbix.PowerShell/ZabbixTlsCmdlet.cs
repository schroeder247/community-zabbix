// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Security;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Provides the base of a command that speaks a framed Zabbix protocol with no connected server: the TLS words, named as
/// Zabbix's agent and proxy configuration names them (<c>TLSPSKIdentity</c>, <c>TLSPSKFile</c>, <c>TLSCertFile</c>,
/// <c>TLSKeyFile</c>, <c>TLSCAFile</c>, <c>TLSCRLFile</c>, <c>TLSServerCertIssuer</c>, <c>TLSServerCertSubject</c>).
/// A listener (<c>Start-ZbxPassiveAgent</c>, <c>Start-ZbxPassiveProxy</c>) derives it directly and adds
/// <c>-TlsAccept</c>; a command that connects derives <see cref="ZabbixWireCmdlet"/>, which adds <c>-Tls</c>.
/// </summary>
/// <remarks>
/// PowerShell composes parameters only through a command's class hierarchy (or <see cref="IDynamicParameters"/>, which
/// the module does not use for always-present parameters: their help would be lost). The commands that talk to a
/// connected server derive <see cref="ZabbixServerCmdlet"/> and so take the same words from
/// <see cref="ZabbixServerWireCmdlet"/>, declared again there; <c>ModuleShapeTests</c> holds both declarations to one
/// shape on every command.
/// </remarks>
public abstract class ZabbixTlsCmdlet : ZabbixCmdlet, IZabbixTlsParameters
{
    /// <summary>
    /// The pre-shared-key identity (Zabbix's <c>TLSPSKIdentity</c>), at most 512 bytes of UTF-8. With <c>-Psk</c>, TLS
    /// with a pre-shared key.
    /// </summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? PskIdentity { get; set; }

    /// <summary>
    /// The pre-shared key, 32 to 512 hex digits (the contents of Zabbix's <c>TLSPSKFile</c>): a secure string
    /// (<c>Read-Host -AsSecureString</c>), or the path of a key file that holds it. It is never written to a stream,
    /// a log or an error.
    /// </summary>
    [Parameter]
    [ZabbixPsk]
    [ValidateNotNull]
    public SecureString? Psk { get; set; }

    /// <summary>
    /// This end's certificate, a PEM file (Zabbix's <c>TLSCertFile</c>), which may carry its chain after it. With
    /// <c>-CertificateKey</c> and <c>-CertificateAuthority</c>, TLS with certificates.
    /// </summary>
    [Parameter]
    [ZabbixFilePath]
    [ValidateNotNullOrEmpty]
    public string? Certificate { get; set; }

    /// <summary>This end's private key, a PEM file (Zabbix's <c>TLSKeyFile</c>).</summary>
    [Parameter]
    [ZabbixFilePath]
    [ValidateNotNullOrEmpty]
    public string? CertificateKey { get; set; }

    /// <summary>
    /// The CA certificates that sign the peer's certificate, a PEM file (Zabbix's <c>TLSCAFile</c>): the only trust
    /// anchor, the operating system's store is not consulted.
    /// </summary>
    [Parameter]
    [ZabbixFilePath]
    [ValidateNotNullOrEmpty]
    public string? CertificateAuthority { get; set; }

    /// <summary>
    /// Certificate revocation lists, a PEM file (Zabbix's <c>TLSCRLFile</c>). Without one, revocation is not checked.
    /// </summary>
    [Parameter]
    [ZabbixFilePath]
    [ValidateNotNullOrEmpty]
    public string? CertificateRevocationList { get; set; }

    /// <summary>
    /// The issuer the peer's certificate must carry, RFC 2253 as OpenSSL prints it (Zabbix's
    /// <c>TLSServerCertIssuer</c>, <c>'CN=Zabbix CA,O=Example'</c>); any issuer the CA signs when omitted.
    /// </summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? ServerIssuer { get; set; }

    /// <summary>
    /// The subject the peer's certificate must carry, RFC 2253 as OpenSSL prints it (Zabbix's
    /// <c>TLSServerCertSubject</c>, <c>'CN=zabbix-server,O=Example'</c>); any subject the CA signs when omitted.
    /// </summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? ServerSubject { get; set; }
}
