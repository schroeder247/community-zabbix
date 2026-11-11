// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Registers a scriptblock as a running passive-proxy session's <c>zabbix.stats</c> answerer.</para>
/// <para type="description">
/// The session keeps no internal counters of its own,
/// so a real answer needs a provider: the scriptblock runs on the session's own dedicated runspace, receiving the
/// request's raw JSON text as its argument; its last output, a dictionary (a <c>@{}</c> hashtable), becomes the
/// reply's <c>data</c> object, key by key.
/// </para>
/// </summary>
/// <remarks>
/// The session has no internal counters to answer from, so the provider's last output, a
/// <see cref="System.Collections.IDictionary"/> (a <c>@{}</c> hashtable), becomes the reply's <c>data</c> object, key
/// by key.
/// </remarks>
[Cmdlet(VerbsLifecycle.Register, "ZbxPassiveProxyStatsProvider")]
[OutputType(typeof(ZabbixPassiveProxySession))]
public sealed class RegisterZbxPassiveProxyStatsProviderCommand : ZabbixCmdlet
{
    /// <summary>The running passive-proxy session's name.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNullOrWhiteSpace]
    public string Name { get; set; } = null!;

    /// <summary>The provider; receives the request's raw JSON text, returns a hashtable of the reply's <c>data</c> object.</summary>
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
        session.RegisterStatsProvider(ScriptBlock);
        if (PassThru) WriteObject(session);
    }
}
