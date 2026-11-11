// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Management.Automation;
using Community.Zabbix.PowerShell.Diagnostics;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Starts a passive agent as a session: a listener that answers the built-in keys to whichever server asks, until stopped.</para>
/// <para type="description">
/// <c>-AllowedPeer</c> defaults to <c>127.0.0.1</c>/<c>::1</c> only — fail-closed, the
/// same as Zabbix's own agent (<c>Server=</c> is never a wildcard by default); give the real server(s) explicitly to
/// accept anything else. Give the agent's credentials (<c>-PskIdentity</c> and <c>-Psk</c>, or <c>-Certificate</c>,
/// <c>-CertificateKey</c> and <c>-CertificateAuthority</c>) to require TLS: the listener then accepts those kinds only,
/// unless <c>-TlsAccept</c> (Zabbix's <c>TLSAccept</c>) says otherwise (<c>-TlsAccept Unencrypted,Psk</c> while a
/// server moves over).
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Start, "ZbxPassiveAgent", SupportsShouldProcess = true)]
[OutputType(typeof(ZabbixAgentSession))]
public sealed class StartZbxPassiveAgentCommand : ZabbixAgentSessionCommand
{
    /// <summary>
    /// Who may connect: IP addresses, CIDR blocks and DNS names (Zabbix's <c>Server=</c> line). Fail-closed by
    /// default: only the loopback addresses, never every peer.
    /// </summary>
    [Parameter(Position = 2)]
    [ValidateNotNullOrEmpty]
    public string[] AllowedPeer { get; set; } = ["127.0.0.1", "::1"];

    /// <summary>The address to bind; every interface by default.</summary>
    [Parameter]
    public string Address { get; set; } = "0.0.0.0";

    /// <summary>The port; the agent port (10050) by default.</summary>
    [Parameter]
    [ValidateRange(0, 65535)]
    public int Port { get; set; } = ZabbixWireClient.DefaultAgentPort;

    /// <summary>How long one key may take when the request does not say: Zabbix's time suffix (<c>'3s'</c>), seconds or a time span.</summary>
    [Parameter]
    [ZabbixDuration]
    public TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(3);

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

    /// <inheritdoc/>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The session is disposed through the async bridge (TryRun) when TryAdd loses the name race, and owned by ZabbixContext.Sessions (disposed on Stop-, Remove-Module or the runspace closing) once TryAdd succeeds; the analyzer cannot see either transfer across the assembly boundary.")]
    protected override void ProcessRecord()
    {
        if (NameInUse<ZabbixAgentSession>()) return;
        if (!ShouldProcess(Name, "Start passive agent")) return;
        if (!TryPluginOptions(out ZabbixPluginOptions? plugins)) return;

        (ZabbixTlsAcceptModes accept, ZabbixTlsOptions? tls) = ZabbixTlsParameters.Listening(this, TlsAccept);
        // The SDK's options, as an application's builder writes them with Configure(...).
        void Configure(ZabbixPassiveAgentOptions options)
        {
            options.HostName = HostName;
            options.Address = Address;
            options.Port = Port;
            options.CheckTimeout = CheckTimeout;
            options.TlsAccept = accept;
            options.Tls = tls;
            foreach (string peer in AllowedPeer) options.AllowedPeers.Add(peer);
            ApplyKeys(options.KeyRules, bundled => options.BundledItems = bundled);
        }

        TimeProvider time = ZabbixContext.Time;
        using Activity? span = ZabbixPowerShellTelemetry.StartCommandSpan(MyInvocation.MyCommand.Name);
        if (TryRun(cancellationToken => ZabbixAgentSession.StartPassiveAsync(Name, Configure, plugins, time, cancellationToken), out ZabbixAgentSession? session, Name)
            && !ZabbixContext.Sessions.TryAdd(session!))
        {
            TryRun(async cancellationToken => { await session!.DisposeAsync().ConfigureAwait(false); return true; }, out _, Name);
            WriteError(new ErrorRecord(new InvalidOperationException($"an agent session named '{Name}' is already running"), "ZabbixAgentNameInUse", ErrorCategory.ResourceExists, Name));
            return;
        }
        if (session is not null && PassThru) WriteObject(session);
    }
}
