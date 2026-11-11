// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Management.Automation;
using System.Net;
using Community.Zabbix.PowerShell.Diagnostics;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Starts an active proxy's uplink as a session: it re-pulls the server's configuration on a cadence, until stopped.</para>
/// <para type="description">
/// The session is kept in this runspace under
/// <c>-Name</c> and pulls the configuration every <c>-RefreshInterval</c> (60 seconds by default; change it live with
/// <c>Set-ZbxActiveProxy</c>). It uploads history only from data providers registered with
/// <c>Register-ZbxActiveProxyDataProvider</c>; to push history yourself, use <c>Send-ZbxProxyData</c>. Pass
/// <c>-PassThru</c> to write the session. A server that requires TLS from the proxy (its <c>tls_accept</c>) is reached
/// with <c>-PskIdentity</c> and <c>-Psk</c>, or the certificate parameters, as the proxy's own <c>TLSConnect</c> would.
/// </para>
/// </summary>
/// <remarks>
/// No data source is registered by default: register one with <c>Register-ZbxActiveProxyDataProvider</c>, or push
/// history with <c>Send-ZbxProxyData</c>.
/// </remarks>
[Cmdlet(VerbsLifecycle.Start, "ZbxActiveProxy", SupportsShouldProcess = true)]
[OutputType(typeof(ZabbixActiveProxySession))]
public sealed class StartZbxActiveProxyCommand : ZabbixWireCmdlet
{
    /// <summary>The name this session is kept under.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrWhiteSpace]
    public string Name { get; set; } = null!;

    /// <summary>The proxy's name as the server knows it.</summary>
    [Parameter(Mandatory = true, Position = 1)]
    [ValidateNotNullOrWhiteSpace]
    public string ProxyName { get; set; } = null!;

    /// <summary>
    /// The server: <c>host</c> or <c>host:port</c> (port 10051 when omitted; an IPv6 address with a port is bracketed,
    /// <c>[::1]:10051</c>). Several are the nodes of one HA cluster, tried in order.
    /// </summary>
    [Parameter(Mandatory = true, Position = 2)]
    [ValidateNotNullOrEmpty]
    [ZabbixEndPoint]
    public DnsEndPoint[] Server { get; set; } = null!;

    /// <summary>How often to re-pull the configuration: Zabbix's time suffix (<c>'1m'</c>), seconds or a time span.</summary>
    [Parameter]
    [ZabbixDuration]
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Writes the session to the pipeline.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (ZabbixContext.Sessions.TryGet(Name, out ZabbixActiveProxySession? _))
        {
            WriteError(new ErrorRecord(new InvalidOperationException($"an active-proxy session named '{Name}' is already running"), "ZabbixActiveProxyNameInUse", ErrorCategory.ResourceExists, Name));
            return;
        }
        if (!ShouldProcess(Name, "Start active proxy")) return;

        TimeProvider time = ZabbixContext.Time;
        IReadOnlyList<DnsEndPoint> nodes = Server;
        using Activity? span = ZabbixPowerShellTelemetry.StartCommandSpan(MyInvocation.MyCommand.Name);
        ZabbixActiveProxySession session;
        try
        {
            session = ZabbixActiveProxySession.Start(Name, ProxyName, nodes, time, RefreshInterval, new ZabbixConnectionOptions { Tls = TlsOptions });
        }
        catch (Exception failure) when (ZabbixTlsParameters.IsTlsFailure(failure))
        {
            // TLS words that do not validate, or certificate files that cannot be read: the session never started.
            WriteError(ZabbixErrors.From(failure, Name));
            return;
        }
        if (!ZabbixContext.Sessions.TryAdd(session))
        {
            TryRun(async _ => await session.DisposeAsync().ConfigureAwait(false), Name);
            WriteError(new ErrorRecord(new InvalidOperationException($"an active-proxy session named '{Name}' is already running"), "ZabbixActiveProxyNameInUse", ErrorCategory.ResourceExists, Name));
            return;
        }
        if (PassThru) WriteObject(session);
    }
}
