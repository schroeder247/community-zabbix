// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;
using System.Security;
using Community.Zabbix.Protocol;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Provides the base of a command that speaks the server's own protocol on its trapper port (<c>queue.get</c>,
/// <c>status.get</c>, <c>item.test</c>, the sender): a connected server's command that also takes the TLS words of
/// <see cref="ZabbixWireCmdlet"/>, named the same. The connection's own (<c>Connect-ZbxServer</c>'s TLS parameters)
/// apply when the command is given none; any one given replaces them for that call.
/// </summary>
/// <remarks>
/// The words are declared again here, not inherited: this command derives <see cref="ZabbixServerCmdlet"/> for its
/// connection, and PowerShell composes parameters only through the class hierarchy (<see cref="ZabbixTlsCmdlet"/>
/// says why not dynamic parameters). <c>ModuleShapeTests</c> holds the two declarations to one shape.
/// </remarks>
public abstract class ZabbixServerWireCmdlet : ZabbixServerCmdlet, IZabbixTlsParameters
{
    /// <summary>
    /// How the connection to the trapper is protected (Zabbix's <c>TLSConnect</c>): <c>Unencrypted</c>,
    /// <c>PreSharedKey</c> (or <c>psk</c>) or <c>Certificate</c> (or <c>cert</c>). When omitted, <c>PreSharedKey</c> if
    /// <c>-Psk</c> or <c>-PskIdentity</c> is given, <c>Certificate</c> if a certificate parameter is; with no TLS
    /// parameter at all, the connection's (<c>Connect-ZbxServer -TrapperTls</c>), unencrypted when it has none.
    /// </summary>
    [Parameter]
    [ZabbixTlsMode]
    public ZabbixTlsMode? Tls { get; set; }

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

    /// <summary>
    /// Whether the connection's TLS words apply when the command is given none: they are the server's trapper's, so a
    /// command whose <c>-Server</c> may name another peer (a proxy's trapper) answers <see langword="false"/> for it.
    /// </summary>
    private protected virtual bool InheritsConnectionTls => true;

    /// <summary>
    /// How the trapper is reached: TLS from this command's words, else the connection's (when
    /// <see cref="InheritsConnectionTls"/>), else none. Set by <see cref="BeginProcessing"/>, on the pipeline thread.
    /// </summary>
    private protected ZabbixConnectionOptions TrapperOptions { get; private set; } = new();

    /// <summary>Requires the connection (<see cref="ZabbixServerCmdlet"/>), then reads the TLS words once: words that cannot be used stop the command.</summary>
    protected override void BeginProcessing()
    {
        base.BeginProcessing();
        try
        {
            ZabbixTlsOptions? given = ZabbixTlsParameters.Connecting(this, Tls);
            TrapperOptions = new ZabbixConnectionOptions { Tls = given ?? (InheritsConnectionTls ? ConnectedOrNull?.TrapperTls : null) };
        }
        catch (ZabbixParameterException failure)
        {
            ThrowTerminatingError(ZabbixErrors.From(failure, nameof(Tls)));
        }
    }

    /// <summary>
    /// Calls the server's own protocol (<c>queue.get</c>, <c>status.get</c>, <c>item.test</c>, …) over a wire to the
    /// nodes <see cref="ZabbixServerCmdlet.ResolveTrapperAsync"/> answers, tried in order, over <see cref="TrapperOptions"/>,
    /// authenticated with this connection's own token (an API token or the login session; <c>trapper_auth.c</c> takes
    /// both, per <c>CZabbixServer.php</c>). The wire and its connection live for the call: disposing them releases what
    /// certificate mode read. Call inside an operation.
    /// </summary>
    /// <typeparam name="T">What the call answers.</typeparam>
    /// <param name="server">The command's <c>-Server</c>, if any.</param>
    /// <param name="call">The call, given the wire and the operation's token.</param>
    /// <param name="cancellationToken">The operation's token.</param>
    /// <returns>The call's answer.</returns>
    private protected async Task<T> CallWireAsync<T>(IReadOnlyList<DnsEndPoint>? server, Func<ZabbixWireClient, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ZabbixConnectionOptions options = TrapperOptions;
        IReadOnlyList<DnsEndPoint> nodes = await ResolveTrapperAsync(server, cancellationToken).ConfigureAwait(false);
        string? token = await Connected.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        using ZabbixWireLink link = ZabbixWireLinks.Open(nodes, options, Connected.Time, Connected.Loggers);
        using var wire = new ZabbixWireClient(link.AsExchange(followRedirects: false), new ZabbixWireOptions { Server = string.Join(';', nodes.Distinct().Select(ZabbixEndPoint.Format)), SessionId = token }, Connected.Loggers, Connected.Time);
        return await call(wire, cancellationToken).ConfigureAwait(false);
    }
}
