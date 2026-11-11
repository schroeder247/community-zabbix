// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.Proxy;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary><para type="synopsis">Builds a <c>proxy data</c> request, for inspection or for a custom send.</para></summary>
[Cmdlet(VerbsCommon.New, "ZbxProxyData")]
[OutputType(typeof(ZabbixProxyDataRequest))]
public sealed class NewZbxProxyDataCommand : ZabbixCmdlet
{
    /// <summary>The proxy's name.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrWhiteSpace]
    public string ProxyName { get; set; } = null!;

    /// <summary>The session the server de-duplicates history on.</summary>
    [Parameter(Mandatory = true, Position = 1)]
    [ValidateNotNullOrWhiteSpace]
    public string Session { get; set; } = null!;

    /// <summary>The values.</summary>
    [Parameter(Position = 2)]
    public ZabbixProxyHistoryItem[]? History { get; set; }

    /// <summary>Whether more is held behind this page.</summary>
    [Parameter]
    public SwitchParameter More { get; set; }

    /// <summary>How old, in seconds, the oldest value left behind is.</summary>
    [Parameter]
    [ValidateRange(0, long.MaxValue)]
    public long ProxyDelay { get; set; }

    /// <summary>The protocol version to announce.</summary>
    [Parameter]
    public string Version { get; set; } = ZabbixProxyUplink.DefaultVersion;

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        DateTimeOffset now = ZabbixContext.Time.GetUtcNow();
        WriteObject(new ZabbixProxyDataRequest("proxy data", ProxyName, Session, History, null, null, null, null, More ? 1 : null, Version, DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()), ProxyDelay > 0 ? ProxyDelay : null));
    }
}
