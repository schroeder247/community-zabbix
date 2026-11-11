// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Management.Automation;
using Community.Zabbix.Hosting;
using Community.Zabbix.PowerShell.Diagnostics;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Starts an active agent as a session: it asks its servers which items to collect, answers the
/// built-in keys, and pushes <c>agent data</c> until stopped.</para>
/// <para type="description">
/// The session is kept in this runspace under
/// <c>-Name</c> (see <c>Get-ZbxActiveAgentStatus</c>, <c>Stop-ZbxActiveAgent</c>); pass <c>-PassThru</c> to also write
/// it. Give <c>-PskIdentity</c> and <c>-Psk</c> to connect with a pre-shared key, or <c>-Certificate</c>,
/// <c>-CertificateKey</c> and <c>-CertificateAuthority</c> to connect with certificates (Zabbix's <c>TLSConnect</c>,
/// <c>-Tls</c>, chooses when both are given).
/// </para>
/// </summary>
/// <remarks>
/// Runs a <see cref="ZabbixActiveAgentDestination"/>; its session state is not persisted.
/// </remarks>
[Cmdlet(VerbsLifecycle.Start, "ZbxActiveAgent", SupportsShouldProcess = true)]
[OutputType(typeof(ZabbixAgentSession))]
public sealed class StartZbxActiveAgentCommand : ZabbixAgentSessionCommand
{
    /// <summary>
    /// Where the agent sends, as Zabbix's <c>ServerActive</c> spells it: <c>host</c> or <c>host:port</c> (port 10051
    /// when omitted; an IPv6 address with a port is bracketed, <c>[::1]:10051</c>). In one string, <c>;</c> separates
    /// the nodes of one HA cluster, tried in order on failover (<c>'zbx1;zbx2'</c>), and <c>,</c> separates independent
    /// destinations, each sent every value (<c>'zbx-a,zbx-b'</c>); quote the text, because PowerShell reads a bare
    /// <c>;</c> as the end of the command. A list (<c>-Server zbx1, zbx2</c>) is the nodes of one HA cluster, as every
    /// other <c>-Server</c> reads it.
    /// </summary>
    [Parameter(Mandatory = true, Position = 2)]
    [ZabbixServerActive]
    [ValidateNotNull]
    public string Server { get; set; } = null!;

    /// <summary>Host metadata for auto-registration action filters.</summary>
    [Parameter]
    public string? HostMetadata { get; set; }

    /// <summary>How often to re-ask which items to collect: Zabbix's time suffix (<c>'5s'</c>), seconds or a time span.</summary>
    [Parameter]
    [ZabbixDuration]
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often to send an empty heartbeat when nothing else is flowing: Zabbix's time suffix (<c>'1m'</c>), seconds or a time span.</summary>
    [Parameter]
    [ZabbixDuration]
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How the connection is protected (Zabbix's <c>TLSConnect</c>): <c>Unencrypted</c>, <c>PreSharedKey</c> (or
    /// <c>psk</c>) or <c>Certificate</c> (or <c>cert</c>). When omitted, <c>PreSharedKey</c> if <c>-Psk</c> or
    /// <c>-PskIdentity</c> is given, <c>Certificate</c> if a certificate parameter is, and unencrypted if neither is.
    /// </summary>
    [Parameter]
    [ZabbixTlsMode]
    public ZabbixTlsMode? Tls { get; set; }

    /// <inheritdoc/>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The session is disposed through the async bridge (TryRun) when TryAdd loses the name race, and owned by ZabbixContext.Sessions (disposed on Stop-, Remove-Module or the runspace closing) once TryAdd succeeds; the analyzer cannot see either transfer across the assembly boundary.")]
    protected override void ProcessRecord()
    {
        if (NameInUse<ZabbixAgentSession>()) return;
        if (!ShouldProcess(Name, "Start active agent")) return;
        if (!TryPluginOptions(out ZabbixPluginOptions? plugins)) return;

        ZabbixTlsOptions? tls;
        try
        {
            tls = ZabbixTlsParameters.Connecting(this, Tls);
        }
        catch (ZabbixParameterException failure)
        {
            WriteError(ZabbixErrors.From(failure, nameof(Tls)));
            return;
        }

        TimeProvider time = ZabbixContext.Time;
        using Activity? span = ZabbixPowerShellTelemetry.StartCommandSpan(MyInvocation.MyCommand.Name);
        // The SDK's options, as an application's builder writes them with Configure(...).
        void Configure(ZabbixActiveAgentOptions options)
        {
            options.HostName = HostName;
            options.HostMetadata = HostMetadata;
            options.ServerActive = Server;
            options.RefreshInterval = RefreshInterval;
            options.HeartbeatInterval = HeartbeatInterval;
            options.Connection.Tls = tls;
            ApplyKeys(options.KeyRules, bundled => options.BundledItems = bundled);
        }
        if (TryRun(cancellationToken => ZabbixAgentSession.StartActiveAsync(Name, Configure, plugins, time, cancellationToken), out ZabbixAgentSession? session, Name)
            && !ZabbixContext.Sessions.TryAdd(session!))
        {
            TryRun(async cancellationToken => { await session!.DisposeAsync().ConfigureAwait(false); return true; }, out _, Name);
            WriteError(new ErrorRecord(new InvalidOperationException($"an agent session named '{Name}' is already running"), "ZabbixAgentNameInUse", ErrorCategory.ResourceExists, Name));
            return;
        }
        if (session is not null && PassThru) WriteObject(session);
    }
}
