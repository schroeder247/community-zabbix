// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.Proxy;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Registers a scriptblock as a data provider on a running active-proxy session: asked for a
/// page before every refresh tick's upload turn.</para>
/// <para type="description">
/// The scriptblock runs on the session's own dedicated
/// runspace, receiving the configuration last pulled as its argument; its last output, when it is a
/// <c>ZabbixProxyDataPage</c>, is pushed (merged with every other registered provider's page) on the session's own
/// refresh cadence (<c>Start-ZbxActiveProxy -RefreshInterval</c> / <c>Set-ZbxActiveProxy -RefreshInterval</c>).
/// Several providers may be registered; each is asked every tick.
/// </para>
/// </summary>
/// <remarks>The page type is <see cref="ZabbixProxyDataPage"/>.</remarks>
[Cmdlet(VerbsLifecycle.Register, "ZbxActiveProxyDataProvider")]
[OutputType(typeof(ZabbixActiveProxySession))]
public sealed class RegisterZbxActiveProxyDataProviderCommand : ZabbixCmdlet
{
    /// <summary>The running active-proxy session's name.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNullOrWhiteSpace]
    public string Name { get; set; } = null!;

    /// <summary>
    /// The provider; receives the configuration last pulled, returns a <c>ZabbixProxyDataPage</c> or nothing.
    /// </summary>
    [Parameter(Mandatory = true, Position = 1)]
    public ScriptBlock ScriptBlock { get; set; } = null!;

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
        session.RegisterDataProvider(ScriptBlock);
        if (PassThru) WriteObject(session);
    }
}
