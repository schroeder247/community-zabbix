// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.Proxy;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Builds one history value entry, for use with <c>New-ZbxProxyData -History</c> or <c>Send-ZbxProxyData</c>.</para>
/// <para type="description">
/// Writes a <c>ZabbixProxyHistoryItem</c>:
/// sequence id, item id, clock, nanoseconds and value. It has no log-item fields (log timestamp, source, severity) and
/// no not-supported flag; the server treats a value of exactly <c>ZBX_NOTSUPPORTED</c> as the item being not supported.
/// </para>
/// </summary>
/// <remarks>
/// <see cref="ZabbixProxyHistoryItem"/> is the wire shape only (id, item id, clock, ns, value). A not-supported item
/// is still sendable, by convention, with <c>-Value</c> carrying the agent's own <c>ZBX_NOTSUPPORTED\0reason</c> text.
/// </remarks>
[Cmdlet(VerbsCommon.New, "ZbxHistoryDataEntry")]
[OutputType(typeof(ZabbixProxyHistoryItem))]
public sealed class NewZbxHistoryDataEntryCommand : ZabbixCmdlet
{
    /// <summary>The item's id.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    public ulong ItemId { get; set; }

    /// <summary>The value.</summary>
    [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true)]
    [AllowEmptyString]
    public string Value { get; set; } = string.Empty;

    /// <summary>When (a date, Unix seconds or <c>clock.ns</c>); now by default.</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    [ZabbixClock]
    public DateTimeOffset? Clock { get; set; }

    /// <summary>The nanoseconds within the second; a stamped sub-second value by default (history tables key on <c>(itemid, clock, ns)</c>, so a constant default would collide same-second pushes).</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    public int? Ns { get; set; }

    /// <summary>The store's sequence id; auto-assigned when omitted.</summary>
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
