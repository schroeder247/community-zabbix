// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.Proxy;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary><para type="synopsis">Builds one history value for <c>proxy data</c>'s <c>history data</c> array.</para></summary>
[Cmdlet(VerbsCommon.New, "ZbxHistoryValue")]
[OutputType(typeof(ZabbixProxyHistoryItem))]
public sealed class NewZbxHistoryValueCommand : ZabbixCmdlet
{
    /// <summary>The item's id.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    public ulong ItemId { get; set; }

    /// <summary>The value, as the wire carries it (numeric items send values as strings too).</summary>
    [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true)]
    [AllowEmptyString]
    public string Value { get; set; } = string.Empty;

    /// <summary>When (a date, Unix seconds or <c>clock.ns</c>); now by default.</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    [ZabbixClock]
    public DateTimeOffset? Clock { get; set; }

    /// <summary>The nanoseconds within the second.</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    public int? Ns { get; set; }

    /// <summary>The store's sequence id the server de-duplicates on; assigned in order when omitted.</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    public long? Id { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        DateTimeOffset clock = Clock ?? ZabbixContext.Time.GetUtcNow();
        WriteObject(new ZabbixProxyHistoryItem(
            Id ?? ZabbixContext.NextProxySequence(),
            ItemId,
            Ns is { } ns ? ZabbixClockAttribute.WithNanoseconds(clock, ns) : clock,
            Value));
    }
}
