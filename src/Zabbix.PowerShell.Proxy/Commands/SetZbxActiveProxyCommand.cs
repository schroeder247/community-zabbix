// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Reconfigures a running active-proxy session's refresh cadence.</para>
/// <para type="description">
/// <c>-RefreshInterval</c> replaces the interval given
/// to <c>Start-ZbxActiveProxy -RefreshInterval</c> while the session keeps running, without losing the configuration
/// it holds. Pass <c>-PassThru</c> to write the session.
/// </para>
/// </summary>
/// <remarks>
/// <see cref="PeriodicTimer.Period"/> is mutable since .NET 8, so the session's own refresh loop is retuned live,
/// without stopping the session or losing its held configuration.
/// </remarks>
[Cmdlet(VerbsCommon.Set, "ZbxActiveProxy", SupportsShouldProcess = true)]
[OutputType(typeof(ZabbixActiveProxySession))]
public sealed class SetZbxActiveProxyCommand : ZabbixCmdlet
{
    /// <summary>The session's name.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNullOrWhiteSpace]
    public string Name { get; set; } = null!;

    /// <summary>How often to re-pull the configuration: Zabbix's time suffix (<c>'1m'</c>), seconds or a time span.</summary>
    [Parameter(Mandatory = true, Position = 1)]
    [ZabbixDuration]
    public TimeSpan RefreshInterval { get; set; }

    /// <summary>Writes the session to the pipeline.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (!ZabbixContext.Sessions.TryGet(Name, out ZabbixActiveProxySession? session))
        {
            WriteError(new ErrorRecord(new ItemNotFoundException($"no active-proxy session is named '{Name}'"), "ZabbixActiveProxyNotFound", ErrorCategory.ObjectNotFound, Name));
            return;
        }
        if (!ShouldProcess(Name, $"Set active proxy refresh interval to {RefreshInterval}")) return;

        session.Retune(RefreshInterval);
        if (PassThru) WriteObject(session);
    }
}
