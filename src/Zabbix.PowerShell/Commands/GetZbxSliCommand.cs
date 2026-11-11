// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets an SLA's SLI report (<c>sla.getsli</c>).</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ZbxSli")]
[OutputType("Community.Zabbix.PowerShell.Sli")]
public sealed class GetZbxSliCommand : ZabbixServerCmdlet
{
    /// <summary>The SLA.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("Id")]
    [ZabbixReference<ZabbixSlaRecord>]
    [ZabbixCompleter<ZabbixSlaRecord>]
    public ZabbixReference<ZabbixSlaRecord>? Sla { get; set; }

    /// <summary>The period's start, in Unix seconds; the SLA's own reporting period when omitted.</summary>
    [Parameter]
    [ZabbixClock]
    public DateTimeOffset? PeriodFrom { get; set; }

    /// <summary>The period's end, in Unix seconds.</summary>
    [Parameter]
    [ZabbixClock]
    public DateTimeOffset? PeriodTo { get; set; }

    /// <summary>How many periods to report.</summary>
    [Parameter]
    public int? Periods { get; set; }

    /// <summary>The services to report on; every service under the SLA when omitted.</summary>
    [Parameter]
    public ulong[]? ServiceId { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (TryRun(async cancellationToken =>
            {
                IReadOnlyList<ulong> ids = await ResolveAsync([Sla!], cancellationToken).ConfigureAwait(false);
                if (ids.Count == 0) return default;
                var request = new ZabbixSlaGetSliRequest
                {
                    SlaId = ids[0],
                    PeriodFrom = PeriodFrom,
                    PeriodTo = PeriodTo,
                    Periods = Periods,
                    ServiceIds = ServiceId,
                };
                return (JsonElement?)await ZabbixClient.Slas.GetSliAsync(request, cancellationToken).ConfigureAwait(false);
            }, out JsonElement? result, Sla) && result is { } sli)
        {
            WriteObject(ZabbixJsonShape.ToShell(sli, "Community.Zabbix.PowerShell.Sli"));
        }
    }
}
