// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;
using System.Security;
using Community.Zabbix.PowerShell.Diagnostics;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Connects the session to a Zabbix server with an endpoint and a credential.</para>
/// <para type="description">
/// Creates the client, asks the server <c>apiinfo.version</c>, proves the credential with one authenticated read (a
/// <c>-Credential</c> logs in for it), and adds the connection to the runspace's context under <c>-Name</c> (the
/// endpoint's host and port by default), as the current server unless <c>-NoDefault</c>; a token or a password the server
/// refuses ends the command with an <c>AuthenticationError</c> that names the server (<c>ZabbixApi.SessionTerminated</c>
/// or <c>ZabbixApi.NotAuthorized</c> for an unknown or disabled token, <c>ZabbixApi.TokenExpired</c>,
/// <c>ZabbixApi.LoginFailed</c>) and adds nothing. A connection already under that name is closed once its in-flight
/// calls finish. Secrets enter only as a
/// <c>PSCredential</c> or a <c>SecureString</c>, never as a positional string. The TLS parameters (<c>-TrapperTls</c>,
/// <c>-TrapperPskIdentity</c>, <c>-TrapperPsk</c>, <c>-TrapperCertificate</c>, …) are the trapper's: every command that
/// speaks the server's own protocol (<c>Send-ZbxData</c>, <c>Get-ZbxQueue</c>, <c>Get-ZbxServerStatus</c>, <c>Test-ZbxItem</c>,
/// <c>Test-ZbxExpression</c>, <c>Test-ZbxMediaType</c>) uses them unless it is given its own. They are checked here
/// (the certificate files are read), and the API's HTTPS is not affected by them.
/// </para>
/// </summary>
/// <remarks>Composes the server through the SDK's <c>AddZabbixClient</c>.</remarks>
[Cmdlet(VerbsCommunications.Connect, "ZbxServer", DefaultParameterSetName = Anonymous)]
[OutputType(typeof(ZabbixConnection))]
public sealed class ConnectZbxServerCommand : ZabbixCmdlet, IZabbixTlsParameters
{
    private const string Anonymous = "Anonymous";
    private const string Token = "ApiToken";
    private const string Login = "Credential";

    /// <summary>The server: its frontend URL (<c>https://zabbix.example/</c>) or its API endpoint (<c>…/api_jsonrpc.php</c>).</summary>
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNull]
    public Uri? Uri { get; set; }

    /// <summary>An API token (<i>Users → API tokens</i>), as a secure string: <c>-ApiToken (Read-Host -AsSecureString)</c>.</summary>
    [Parameter(Mandatory = true, ParameterSetName = Token)]
    [ValidateNotNull]
    public SecureString? ApiToken { get; set; }

    /// <summary>A user name and password: the command logs in (<c>user.login</c>) to prove them, and the session id is the token from then on.</summary>
    [Parameter(Mandatory = true, ParameterSetName = Login)]
    [Credential]
    [ValidateNotNull]
    public PSCredential? Credential { get; set; }

    /// <summary>The name the session knows this server by; the endpoint's host and port when omitted.</summary>
    [Parameter]
    [ValidateNotNullOrWhiteSpace]
    public string? Name { get; set; }

    /// <summary>
    /// The server's trapper as this machine reaches it, for the commands that speak the server's own protocol: <c>host</c>
    /// or <c>host:port</c> (port 10051 when omitted; an IPv6 address with a port is bracketed, <c>[::1]:10051</c>).
    /// Several are the nodes of one HA cluster, tried in order. Omitted: those commands ask the API for the active HA
    /// node, as the frontend does.
    /// </summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    [ZabbixEndPoint]
    public DnsEndPoint[]? Trapper { get; set; }

    /// <summary>
    /// The web frontend, when it is not where the API is (<c>https://zabbix.example/</c>): the base of the pages
    /// <c>Show-ZbxPage</c> opens. Omitted, the API endpoint's folder.
    /// </summary>
    [Parameter]
    [ValidateNotNull]
    public Uri? FrontendUri { get; set; }

    /// <summary>
    /// Accepts the frontend's certificate without checking it (a self-signed or internal certificate), as
    /// <c>Invoke-RestMethod -SkipCertificateCheck</c> does; a warning says so. The trapper's TLS is not affected.
    /// </summary>
    [Parameter]
    public SwitchParameter SkipCertificateCheck { get; set; }

    /// <summary>
    /// Sends the API token or the password to an endpoint that is plain <c>http</c> on another host, where it crosses the
    /// network in clear; without it such a connection is refused. A loopback endpoint never needs it. A warning says so.
    /// </summary>
    [Parameter]
    public SwitchParameter AllowUnencryptedCredential { get; set; }

    /// <summary>How long, in seconds, one API request may take, retries included; the SDK's deadlines (10 s per attempt, 30 s in all) when omitted.</summary>
    [Parameter]
    [ValidateRange(1, 86400)]
    public int? TimeoutSec { get; set; }

    /// <summary>The HTTP proxy the API is reached through (<c>http://proxy.example:3128</c>); the system's proxy when omitted.</summary>
    [Parameter]
    [ValidateNotNull]
    public Uri? Proxy { get; set; }

    /// <summary>The proxy's credential, when it asks for one.</summary>
    [Parameter]
    [Credential]
    [ValidateNotNull]
    public PSCredential? ProxyCredential { get; set; }

    /// <summary>Adds the server without making it the current one (a first server always becomes current).</summary>
    [Parameter]
    public SwitchParameter NoDefault { get; set; }

    /// <summary>Writes the connection to the pipeline.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    private static ZabbixSessionCredential Session(PSCredential credential)
        => new(credential.UserName, _ => ValueTask.FromResult(credential.GetNetworkCredential().Password));

    // apiinfo.version takes no token, so it proves nothing of the credential: one authenticated read does (a session
    // credential logs in for it). Zabbix authenticates before it checks the method against the user's role
    // (CLocalApiClient.php: authenticate precedes the role's API access check), so any answer but a refusal of the
    // credential itself (a role that may not call user.get answers Permission) proves the token or the login good.
    private static async Task VerifyAsync(ZabbixClient client, CancellationToken cancellationToken)
    {
        try
        {
            await client.Users.GetAsync(new ZabbixUserGetParameters { Output = [ZabbixUserFields.UserId], Limit = 1 }, cancellationToken).ConfigureAwait(false);
        }
        catch (ZabbixApiException answered) when (!IsRefusal(answered.Kind))
        {
            // Authenticated: what the role allows is for the commands that follow to find out.
        }
    }

    private static bool IsRefusal(ZabbixApiErrorKind kind)
        => kind is ZabbixApiErrorKind.NotAuthorized or ZabbixApiErrorKind.SessionTerminated or ZabbixApiErrorKind.TokenExpired or ZabbixApiErrorKind.LoginFailed;

    /// <summary>
    /// A credential the server refused ends the command: nothing is added to the context, and the record says which
    /// credential was refused by which server. Its id stays the API's (<c>ZabbixApi.SessionTerminated</c>,
    /// <c>ZabbixApi.LoginFailed</c>, …), its category <c>AuthenticationError</c>.
    /// </summary>
    /// <param name="failure">The operation's failure.</param>
    /// <param name="target">The endpoint.</param>
    private protected override ErrorRecord? Terminating(Exception failure, object? target)
    {
        if (failure is not ZabbixApiException refused || !IsRefusal(refused.Kind) || ParameterSetName == Anonymous) return null;
        string server = target is Uri endpoint ? ZabbixEndpoint.Shown(endpoint) : "the server";
        string answered = refused.Detail ?? refused.ServerMessage ?? refused.Message;
        (string message, string action) = (ParameterSetName, refused.Kind) switch
        {
            (Token, ZabbixApiErrorKind.TokenExpired) => (
                $"{server} refused the API token: it has expired (Zabbix answered \"{answered}\"). The connection was not added.",
                "Generate a new token, or move this one's expiry date, under Users > API tokens."),
            (Token, _) => (
                $"{server} refused the API token: the server does not know it, or it is disabled (Zabbix answered \"{answered}\"). The connection was not added.",
                "Pass the token's secret, which the server shows once when it generates it (Users > API tokens, or New-ZbxToken -Generate), not the token's name or id; check that the token is enabled on this server."),
            (_, ZabbixApiErrorKind.LoginFailed) => (
                $"{server} refused the user name and password (Zabbix answered \"{answered}\"). The connection was not added.",
                "Check the user name and password; after repeated failures Zabbix blocks the account for a while, so wait before trying again."),
            _ => (
                $"{server} refused the credential (Zabbix answered \"{answered}\"). The connection was not added.",
                "Check the user name and password, and that the user may use the API."),
        };
        ErrorRecord record = ZabbixErrors.From(refused, target);
        record.ErrorDetails = new ErrorDetails(message) { RecommendedAction = action };
        return record;
    }

    /// <summary>
    /// How the trapper connection is protected (Zabbix's <c>TLSConnect</c>): <c>Unencrypted</c>, <c>PreSharedKey</c> (or
    /// <c>psk</c>) or <c>Certificate</c> (or <c>cert</c>). When omitted, <c>PreSharedKey</c> if <c>-TrapperPsk</c> or
    /// <c>-TrapperPskIdentity</c> is given, <c>Certificate</c> if a certificate parameter is, and unencrypted if neither is.
    /// </summary>
    [Parameter]
    [ZabbixTlsMode]
    public ZabbixTlsMode? TrapperTls { get; set; }

    /// <summary>The trapper's pre-shared-key identity (Zabbix's <c>TLSPSKIdentity</c>), at most 512 bytes of UTF-8.</summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? TrapperPskIdentity { get; set; }

    /// <summary>
    /// The trapper's pre-shared key, 32 to 512 hex digits (the contents of Zabbix's <c>TLSPSKFile</c>): a secure string,
    /// or the path of a key file that holds it. It is never written to a stream, a log or an error.
    /// </summary>
    [Parameter]
    [ZabbixPsk]
    [ValidateNotNull]
    public SecureString? TrapperPsk { get; set; }

    /// <summary>This end's certificate toward the trapper, a PEM file (Zabbix's <c>TLSCertFile</c>); not the API's HTTPS.</summary>
    [Parameter]
    [ZabbixFilePath]
    [ValidateNotNullOrEmpty]
    public string? TrapperCertificate { get; set; }

    /// <summary>This end's private key toward the trapper, a PEM file (Zabbix's <c>TLSKeyFile</c>).</summary>
    [Parameter]
    [ZabbixFilePath]
    [ValidateNotNullOrEmpty]
    public string? TrapperCertificateKey { get; set; }

    /// <summary>The CA certificates that sign the trapper's certificate, a PEM file (Zabbix's <c>TLSCAFile</c>).</summary>
    [Parameter]
    [ZabbixFilePath]
    [ValidateNotNullOrEmpty]
    public string? TrapperCertificateAuthority { get; set; }

    /// <summary>Certificate revocation lists for the trapper's certificate, a PEM file (Zabbix's <c>TLSCRLFile</c>).</summary>
    [Parameter]
    [ZabbixFilePath]
    [ValidateNotNullOrEmpty]
    public string? TrapperCertificateRevocationList { get; set; }

    /// <summary>The issuer the trapper's certificate must carry (Zabbix's <c>TLSServerCertIssuer</c>).</summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? TrapperServerIssuer { get; set; }

    /// <summary>The subject the trapper's certificate must carry (Zabbix's <c>TLSServerCertSubject</c>).</summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? TrapperServerSubject { get; set; }

    string? IZabbixTlsParameters.PskIdentity => TrapperPskIdentity;

    SecureString? IZabbixTlsParameters.Psk => TrapperPsk;

    string? IZabbixTlsParameters.Certificate => TrapperCertificate;

    string? IZabbixTlsParameters.CertificateKey => TrapperCertificateKey;

    string? IZabbixTlsParameters.CertificateAuthority => TrapperCertificateAuthority;

    string? IZabbixTlsParameters.CertificateRevocationList => TrapperCertificateRevocationList;

    string? IZabbixTlsParameters.ServerIssuer => TrapperServerIssuer;

    string? IZabbixTlsParameters.ServerSubject => TrapperServerSubject;

    private ZabbixTlsOptions? TlsOptions { get; set; }

    /// <summary>Reads the trapper's TLS words once, before any record: words that cannot be used stop the command (<c>ZabbixTlsModeAmbiguous</c>).</summary>
    protected override void BeginProcessing()
    {
        base.BeginProcessing();
        try
        {
            TlsOptions = ZabbixTlsParameters.Connecting(this, TrapperTls);
        }
        catch (ZabbixParameterException failure)
        {
            ThrowTerminatingError(ZabbixErrors.From(failure, nameof(TrapperTls)));
        }
    }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        Uri endpoint;
        try
        {
            endpoint = ZabbixEndpoint.Of(Uri!);
        }
        catch (ArgumentException failure)
        {
            // One InvalidArgument record naming the URL as it may be shown.
            ThrowTerminatingError(ZabbixErrors.From(failure, ZabbixEndpoint.Shown(Uri!)));
            return;
        }
        ZabbixTlsOptions? trapperTls = TlsOptions;
        if (trapperTls is not null)
        {
            try
            {
                ZabbixTlsParameters.Check(trapperTls, ZabbixContext.Time);
            }
            catch (Exception failure) when (ZabbixTlsParameters.IsTlsFailure(failure))
            {
                ThrowTerminatingError(ZabbixErrors.From(failure, nameof(TrapperTls)));
                return;
            }
        }
        string name = Name ?? endpoint.Authority;
        ZabbixCredential credential = ParameterSetName switch
        {
            // NetworkCredential is the BCL's SecureString → string conversion; the string lives only inside the credential.
            Token => new ZabbixApiTokenCredential(new NetworkCredential(string.Empty, ApiToken).Password),
            // The password stays in the PSCredential's SecureString and is read at each login, not held as a string.
            Login => Session(Credential!),
            _ => ZabbixCredential.Anonymous,
        };

        ZabbixConnection? connection = null;
        try
        {
            if (SkipCertificateCheck) WriteWarning($"The certificate of {ZabbixEndpoint.Shown(endpoint)} is not checked: anyone between this machine and the server can read and change its traffic.");
            if (AllowUnencryptedCredential) WriteWarning($"Credentials for {ZabbixEndpoint.Shown(endpoint)} may cross the network in clear: anyone between this machine and the server can read them.");
            var http = new ZabbixHttpSettings(SkipCertificateCheck, TimeoutSec is { } seconds ? TimeSpan.FromSeconds(seconds) : null, Proxy, ProxyCredential?.GetNetworkCredential());
            connection = ZabbixConnection.Open(name, endpoint, credential, ZabbixContext.ExportsTelemetry, ZabbixContext.Time, FrontendUri, http, AllowUnencryptedCredential);
            ZabbixConnection opened = connection;
            if (!TryRun(opened, async cancellationToken =>
                {
                    Version version = await opened.Client.GetApiVersionAsync(cancellationToken).ConfigureAwait(false);
                    if (ParameterSetName != Anonymous) await VerifyAsync(opened.Client, cancellationToken).ConfigureAwait(false);
                    ZabbixPowerShellLog.Connected(opened.Log, name, endpoint, version);
                    return version;
                }, out Version? version, endpoint))
            {
                return;
            }

            opened.ApiVersion = version;
            opened.Trapper = Trapper;
            opened.TrapperTls = trapperTls;
            ZabbixContext.Add(opened, keepCurrent: NoDefault);
            connection = null;                                   // the context owns it now
            if (PassThru) WriteObject(opened);
        }
        catch (Exception failure) when (failure is ArgumentException or OptionsValidationException)
        {
            ThrowTerminatingError(ZabbixErrors.From(failure, endpoint));
        }
        finally
        {
            connection?.Dispose();
        }
    }
}
