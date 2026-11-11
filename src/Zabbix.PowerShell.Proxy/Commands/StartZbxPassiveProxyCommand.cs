// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Management.Automation;
using Community.Zabbix.PowerShell.Diagnostics;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Starts a passive proxy's listener as a session: the server pushes configuration and pulls history until stopped.</para>
/// <para type="description">
/// <c>-AllowedPeer</c> is required: the listener
/// accepts connections only from the peers it names. <c>-Version</c> is required: the server's major.minor, which it
/// never tells a passive proxy and without which it never configures one. Write history for the server to pull with
/// <c>Send-ZbxPassiveProxyHistory</c>. To take the server's connections over TLS, give the proxy's credentials
/// (<c>-PskIdentity</c> and <c>-Psk</c>, or <c>-Certificate</c>, <c>-CertificateKey</c> and <c>-CertificateAuthority</c>):
/// the listener then accepts those kinds only, unless <c>-TlsAccept</c> says otherwise.
/// </para>
/// </summary>
/// <remarks><c>-AllowedPeer</c> is required, as the SDK's listener requires it; <c>-Version</c> is required, as the SDK's
/// passive proxy requires it (the server never says its own).</remarks>
[Cmdlet(VerbsLifecycle.Start, "ZbxPassiveProxy", SupportsShouldProcess = true)]
[OutputType(typeof(ZabbixPassiveProxySession))]
public sealed class StartZbxPassiveProxyCommand : ZabbixTlsCmdlet
{
    /// <summary>The name this session is kept under.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrWhiteSpace]
    public string Name { get; set; } = null!;

    /// <summary>Who may connect: IP addresses, CIDR blocks and DNS names (Zabbix's <c>Server=</c> line).</summary>
    [Parameter(Mandatory = true, Position = 1)]
    [ValidateNotNullOrEmpty]
    public string[] AllowedPeer { get; set; } = null!;

    /// <summary>The address to bind; every interface by default.</summary>
    [Parameter]
    public string Address { get; set; } = "0.0.0.0";

    /// <summary>The port; the proxy's trapper port (10051) by default.</summary>
    [Parameter]
    [ValidateRange(0, 65535)]
    public int Port { get; set; } = ZabbixWireClient.DefaultServerPort;

    /// <summary>
    /// The version the proxy announces: the server's own major.minor (e.g. <c>7.4</c>). No server message tells a passive
    /// proxy the server's version, and a server configures only a proxy of its own major.minor, so there is no default.
    /// </summary>
    [Parameter(Mandatory = true)]
    public Version Version { get; set; } = null!;

    /// <summary>
    /// Which connections are accepted (Zabbix's <c>TLSAccept</c>): <c>Unencrypted</c>, <c>Psk</c>, <c>Certificate</c>,
    /// several as a list (<c>-TlsAccept Unencrypted,Psk</c>). When omitted, the kinds the credentials given allow
    /// (<c>Psk</c> with <c>-Psk</c>, <c>Certificate</c> with <c>-Certificate</c>), and unencrypted only when none is given.
    /// </summary>
    /// <remarks>
    /// An array so <c>-TlsAccept Unencrypted,Psk</c> binds: <c>ZabbixTlsAcceptModes</c> is <c>[Flags]</c>, but
    /// PowerShell only ORs several values together when the parameter's own type is an array; a scalar
    /// <c>ZabbixTlsAcceptModes?</c> rejects an array literal with "Cannot convert 'System.Object[]'".
    /// </remarks>
    [Parameter]
    [ValidateNotNullOrEmpty]
    public ZabbixTlsAcceptModes[]? TlsAccept { get; set; }

    /// <summary>Writes the session to the pipeline.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <inheritdoc/>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The session is disposed through the async bridge (TryRun) when TryAdd loses the name race, and owned by ZabbixContext.Sessions (disposed on Stop-, Remove-Module or the runspace closing) once TryAdd succeeds; the analyzer cannot see either transfer across the assembly boundary.")]
    protected override void ProcessRecord()
    {
        if (ZabbixContext.Sessions.TryGet(Name, out ZabbixPassiveProxySession? _))
        {
            WriteError(new ErrorRecord(new InvalidOperationException($"a passive-proxy session named '{Name}' is already running"), "ZabbixPassiveProxyNameInUse", ErrorCategory.ResourceExists, Name));
            return;
        }
        if (!ShouldProcess(Name, "Start passive proxy")) return;

        (ZabbixTlsAcceptModes accept, ZabbixTlsOptions? tls) = ZabbixTlsParameters.Listening(this, TlsAccept);
        // The SDK's options, as an application's builder writes them with Configure(...).
        void Configure(ZabbixProxyOptions options)
        {
            options.Version = Version.ToString();
            options.Trapper.Address = Address;
            options.Trapper.Port = Port;
            foreach (string peer in AllowedPeer) options.Trapper.AllowedPeers.Add(peer);
            (options.Trapper.TlsAccept, options.Trapper.Tls) = (accept, tls);
        }

        TimeProvider time = ZabbixContext.Time;
        using Activity? span = ZabbixPowerShellTelemetry.StartCommandSpan(MyInvocation.MyCommand.Name);
        if (TryRun(cancellationToken => ZabbixPassiveProxySession.StartAsync(Name, Configure, time, cancellationToken), out ZabbixPassiveProxySession? session, Name)
            && !ZabbixContext.Sessions.TryAdd(session!))
        {
            TryRun(async _ => await session!.DisposeAsync().ConfigureAwait(false), Name);
            WriteError(new ErrorRecord(new InvalidOperationException($"a passive-proxy session named '{Name}' is already running"), "ZabbixPassiveProxyNameInUse", ErrorCategory.ResourceExists, Name));
            return;
        }
        if (session is not null && PassThru) WriteObject(session);
    }
}
