// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Updates the server's housekeeping settings (<c>housekeeping.update</c>).</para>
/// </summary>
[Cmdlet(VerbsCommon.Set, "ZbxHousekeeping", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium)]
[OutputType(typeof(ZabbixHousekeepingSettings))]
public sealed class SetZbxHousekeepingCommand : ZabbixSingletonSetCmdlet<ZabbixHousekeepingUpdateRequest>
{
    /// <summary>Whether events are removed by an age (<c>hk_events_mode</c>).</summary>
    [Parameter] public bool? HousekeepingEventsMode { get; set; }

    /// <summary>How long trigger events are kept, e.g. <c>365d</c> (<c>hk_events_trigger</c>).</summary>
    [Parameter] public string? HousekeepingEventsTrigger { get; set; }

    /// <summary>How long service events are kept (<c>hk_events_service</c>).</summary>
    [Parameter] public string? HousekeepingEventsService { get; set; }

    /// <summary>How long internal events are kept (<c>hk_events_internal</c>).</summary>
    [Parameter] public string? HousekeepingEventsInternal { get; set; }

    /// <summary>How long discovery events are kept (<c>hk_events_discovery</c>).</summary>
    [Parameter] public string? HousekeepingEventsDiscovery { get; set; }

    /// <summary>How long autoregistration events are kept (<c>hk_events_autoreg</c>).</summary>
    [Parameter] public string? HousekeepingEventsAutoregistration { get; set; }

    /// <summary>Whether old service data is removed by an age (<c>hk_services_mode</c>).</summary>
    [Parameter] public bool? HousekeepingServicesMode { get; set; }

    /// <summary>How long service data is kept (<c>hk_services</c>).</summary>
    [Parameter] public string? HousekeepingServices { get; set; }

    /// <summary>Whether the audit log is removed by an age (<c>hk_audit_mode</c>).</summary>
    [Parameter] public bool? HousekeepingAuditMode { get; set; }

    /// <summary>How long the audit log is kept (<c>hk_audit</c>).</summary>
    [Parameter] public string? HousekeepingAudit { get; set; }

    /// <summary>Whether old user sessions are removed by an age (<c>hk_sessions_mode</c>).</summary>
    [Parameter] public bool? HousekeepingSessionsMode { get; set; }

    /// <summary>How long user sessions are kept (<c>hk_sessions</c>).</summary>
    [Parameter] public string? HousekeepingSessions { get; set; }

    /// <summary>Whether history is removed by an age (<c>hk_history_mode</c>).</summary>
    [Parameter] public bool? HousekeepingHistoryMode { get; set; }

    /// <summary>Whether every item uses the global history period, rather than its own (<c>hk_history_global</c>).</summary>
    [Parameter] public bool? HousekeepingHistoryGlobal { get; set; }

    /// <summary>The global history period (<c>hk_history</c>).</summary>
    [Parameter] public string? HousekeepingHistory { get; set; }

    /// <summary>Whether trends are removed by an age (<c>hk_trends_mode</c>).</summary>
    [Parameter] public bool? HousekeepingTrendsMode { get; set; }

    /// <summary>Whether every item uses the global trend period, rather than its own (<c>hk_trends_global</c>).</summary>
    [Parameter] public bool? HousekeepingTrendsGlobal { get; set; }

    /// <summary>The global trend period (<c>hk_trends</c>).</summary>
    [Parameter] public string? HousekeepingTrends { get; set; }

    /// <summary>Whether history/trend compression is enabled (<c>compression_status</c>).</summary>
    [Parameter] public bool? CompressionStatus { get; set; }

    /// <summary>How old data must be before it is compressed (<c>compress_older</c>).</summary>
    [Parameter] public string? CompressOlder { get; set; }

    /// <inheritdoc/>
    protected override string Subject => "the housekeeping settings";

    /// <inheritdoc/>
    protected override ZabbixHousekeepingUpdateRequest BuildRequest() => new()
    {
        HousekeepingEventsMode = HousekeepingEventsMode,
        HousekeepingEventsTrigger = HousekeepingEventsTrigger,
        HousekeepingEventsService = HousekeepingEventsService,
        HousekeepingEventsInternal = HousekeepingEventsInternal,
        HousekeepingEventsDiscovery = HousekeepingEventsDiscovery,
        HousekeepingEventsAutoregistration = HousekeepingEventsAutoregistration,
        HousekeepingServicesMode = HousekeepingServicesMode,
        HousekeepingServices = HousekeepingServices,
        HousekeepingAuditMode = HousekeepingAuditMode,
        HousekeepingAudit = HousekeepingAudit,
        HousekeepingSessionsMode = HousekeepingSessionsMode,
        HousekeepingSessions = HousekeepingSessions,
        HousekeepingHistoryMode = HousekeepingHistoryMode,
        HousekeepingHistoryGlobal = HousekeepingHistoryGlobal,
        HousekeepingHistory = HousekeepingHistory,
        HousekeepingTrendsMode = HousekeepingTrendsMode,
        HousekeepingTrendsGlobal = HousekeepingTrendsGlobal,
        HousekeepingTrends = HousekeepingTrends,
        CompressionStatus = CompressionStatus,
        CompressOlder = CompressOlder,
    };

    /// <inheritdoc/>
    protected override async Task<object?> UpdateAsync(ZabbixClient client, ZabbixHousekeepingUpdateRequest request, CancellationToken cancellationToken)
        => await client.Housekeeping.UpdateAsync(request, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    protected override async Task<object?> GetAsync(CancellationToken cancellationToken)
        => await ZabbixClient.Housekeeping.GetAsync(cancellationToken).ConfigureAwait(false);
}
