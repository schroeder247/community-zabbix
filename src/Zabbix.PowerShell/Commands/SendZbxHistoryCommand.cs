// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Backfills historical values through the API (<c>history.push</c>) — for a known past clock, unlike <c>Send-ZbxData</c>'s live trapper push.</para>
/// </summary>
[Cmdlet(VerbsCommunications.Send, "ZbxHistory", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Low)]
[OutputType(typeof(ZabbixHistoryPushResult))]
public sealed class SendZbxHistoryCommand : ZabbixServerCmdlet
{
    /// <summary>The host the value belongs to.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [Alias("Host")]
    [ValidateNotNullOrWhiteSpace]
    public string? HostName { get; set; }

    /// <summary>The item key.</summary>
    [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNullOrWhiteSpace]
    [ZabbixCompleter<ZabbixItem>]
    public string? Key { get; set; }

    /// <summary>The value, as given: a string is sent as that string (never read as JSON), a number as a number.</summary>
    [Parameter(Mandatory = true, Position = 2, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNull]
    [AllowEmptyString]
    public object? Value { get; set; }

    /// <summary>When the value was collected (a date, Unix seconds or <c>clock.ns</c>) — the point of backfilling rather than sending live.</summary>
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)]
    [ZabbixClock]
    public DateTimeOffset Clock { get; set; }

    /// <summary>Nanoseconds within the second; the clock's own when omitted.</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    public int? Ns { get; set; }

    private readonly List<ZabbixHistoryPushRequest> _values = [];

    /// <inheritdoc/>
    protected override void ProcessRecord()
        => _values.Add(new ZabbixHistoryPushRequest { Host = HostName, Key = Key, Value = ZabbixJsonShape.FromValue(Value), Clock = Ns is { } ns ? ZabbixClockAttribute.WithNanoseconds(Clock, ns) : Clock });

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (_values.Count == 0) return;
        if (!ShouldProcess($"{_values.Count} value(s)", "Push history")) return;
        if (TryRun(async cancellationToken => await ZabbixClient.History.PushAsync(_values, cancellationToken).ConfigureAwait(false), out ZabbixHistoryPushResult? result, Connected.Name))
            WriteObject(result);
    }
}
