// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.Proxy;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Registers a scriptblock as a data provider on a running passive-proxy session: asked for a
/// page before every <c>proxy data</c> pull.</para>
/// <para type="description">
/// The scriptblock runs on the session's own dedicated
/// runspace, receiving the configuration held as its argument; its last output, when it is a
/// <c>ZabbixProxyDataPage</c> (built with <c>New-ZbxHistoryValue</c> etc. and
/// <c>[Community.Zabbix.Proxy.ZabbixProxyDataPage]::new(...)</c>), is merged into the page the server pulls next. Several
/// providers may be registered; each is called every turn.
/// </para>
/// </summary>
/// <remarks>The page type is <see cref="ZabbixProxyDataPage"/>.</remarks>
[Cmdlet(VerbsLifecycle.Register, "ZbxPassiveProxyDataProvider")]
[OutputType(typeof(ZabbixPassiveProxySession))]
public sealed class RegisterZbxPassiveProxyDataProviderCommand : ZabbixCmdlet
{
    /// <summary>The running passive-proxy session's name.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNullOrWhiteSpace]
    public string Name { get; set; } = null!;

    /// <summary>
    /// The provider; receives the held configuration, returns a <c>ZabbixProxyDataPage</c> or nothing.
    /// </summary>
    [Parameter(Mandatory = true, Position = 1)]
    public ScriptBlock ScriptBlock { get; set; } = null!;

    /// <summary>Writes the session to the pipeline.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (!ZabbixContext.Sessions.TryGet(Name, out ZabbixPassiveProxySession? session))
        {
            WriteError(new ErrorRecord(new ItemNotFoundException($"no passive-proxy session is named '{Name}'"), "ZabbixPassiveProxyNotFound", ErrorCategory.ObjectNotFound, Name));
            return;
        }
        session.RegisterDataProvider(ScriptBlock);
        if (PassThru) WriteObject(session);
    }
}
