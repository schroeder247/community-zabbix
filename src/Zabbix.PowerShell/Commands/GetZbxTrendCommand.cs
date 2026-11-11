// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets hourly trend records (<c>trend.get</c>): the min/avg/max Zabbix keeps once raw history ages out.</para>
/// <para type="description">
/// For the items in <c>-Item</c> (ids, names or objects; every item when omitted), within the <c>-From</c> to
/// <c>-Till</c> window (unbounded on a side that is omitted), at most <c>-Limit</c> records. There is no
/// <c>-Filter</c> or search selection.
/// </para>
/// </summary>
/// <remarks>
/// Hand-written: <c>ZabbixTrendGetParameters</c> has neither <c>filter</c> nor <c>search</c> (only <c>itemids</c> and
/// the <c>time_from</c>/<c>time_till</c> window), so it cannot implement <see cref="IZabbixGetParameters"/>.
/// <c>trend.get</c> itself does not require <c>itemids</c> (<c>CTrend.php</c>: <c>API_ALLOW_NULL</c>,
/// default <see langword="null"/>).
/// </remarks>
[Cmdlet(VerbsCommon.Get, "ZbxTrend")]
[OutputType(typeof(ZabbixTrendRecord))]
public sealed class GetZbxTrendCommand : ZabbixServerCmdlet
{
    /// <summary>The items to get trends for: ids, names or objects; every item's trends when omitted.</summary>
    /// <remarks><c>trend.get</c> requires no <c>itemids</c>.</remarks>
    [Parameter(Position = 0, ValueFromPipelineByPropertyName = true)]
    [Alias("ItemId")]
    [ZabbixReference<ZabbixItem>]
    [ZabbixCompleter<ZabbixItem>]
    public ZabbixReference<ZabbixItem>[]? Item { get; set; }

    /// <summary>The window's start; every trend up to <c>-Till</c> when omitted.</summary>
    [Parameter]
    [ZabbixClock]
    public DateTimeOffset? From { get; set; }

    /// <summary>The window's end; every trend from <c>-From</c> onward when omitted.</summary>
    [Parameter]
    [ZabbixClock]
    public DateTimeOffset? Till { get; set; }

    /// <summary>At most this many.</summary>
    [Parameter]
    [ValidateRange(1, int.MaxValue)]
    public int? Limit { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        TryRun(async cancellationToken =>
        {
            IReadOnlyList<ulong>? ids = Item is null ? null : await ResolveAsync(Item, cancellationToken).ConfigureAwait(false);
            if (ids is { Count: 0 }) return (IReadOnlyList<ZabbixTrendRecord>)[];
            var parameters = new ZabbixTrendGetParameters
            {
                ItemIds = ids is null ? null : [.. ids],
                TimeFrom = From,
                TimeTill = Till,
                Limit = Limit,
            };
            return await ZabbixClient.Trends.GetAsync(parameters, cancellationToken).ConfigureAwait(false);
        }, out IReadOnlyList<ZabbixTrendRecord>? trends, Item);
        if (trends is not null) WriteObject(trends, enumerateCollection: true);
    }
}
