// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Pushes a running agent session's handler keys onto its host as items, adding whatever is
/// missing and, with <c>-RemoveStale</c>, removing items it created for a key the session no longer
/// answers.</para>
/// <para type="description">
/// The host must already have been provisioned on this
/// session by <c>Initialize-ZbxAgentHost</c>. <c>-RemoveStale</c> only removes items that
/// <c>Initialize-</c>/<c>Publish-ZbxAgentHost</c> created, never an item something else put on the host. Writes the
/// host's status only with <c>-PassThru</c>. Supports <c>-WhatIf</c>/<c>-Confirm</c> (ConfirmImpact Medium).
/// </para>
/// </summary>
/// <remarks>
/// Like <c>Clear-ZbxAgentHost</c>, this needs the session's bookkeeping: <c>-RemoveStale</c> may only remove what it
/// tracks.
/// </remarks>
[Cmdlet(VerbsData.Publish, "ZbxAgentHost", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium)]
[OutputType(typeof(ZabbixAgentHostStatus))]
public sealed class PublishZbxAgentHostCommand : ZabbixAgentHostCommand
{
    /// <summary>
    /// Removes items <c>Initialize-</c>/<c>Publish-ZbxAgentHost</c> created for a key the session no longer answers.
    /// </summary>
    [Parameter]
    public SwitchParameter RemoveStale { get; set; }

    /// <summary>
    /// Value type for a newly created item whose key is not a built-in one. Default: <c>Text</c>, which stores any
    /// answer; give <c>Unsigned</c> or <c>Float</c> for keys that answer numbers, to graph them. The built-in keys
    /// always get their own (<c>agent.ping</c> <c>Unsigned</c>; <c>agent.version</c>, <c>agent.hostname</c> and
    /// <c>system.uname</c> <c>Character</c>). Ignored for an item that already exists.
    /// </summary>
    [Parameter]
    public ZabbixItemValueType ValueType { get; set; } = ZabbixAgentHostProvisioning.DefaultValueType;

    /// <summary>Emits the resulting host status.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (!TryGetSession(out ZabbixAgentSession session)) return;
        if (!TryGetRecord(session, out ZabbixAgentHostRecord? record)) return;
        if (!ShouldProcess(record.HostName, "Publish agent host")) return;

        List<string> keys = [.. session.ProvisionedKeys];
        if (!TryRun(async cancellationToken =>
        {
            Dictionary<string, ulong> itemIds = await ZabbixAgentHostProvisioning.EnsureItemsAsync(
                ZabbixClient, record.HostId, keys, ValueType, ZabbixAgentHostProvisioning.ItemTypeOf(session), record.InterfaceId, cancellationToken).ConfigureAwait(false);
            foreach ((string key, ulong itemId) in itemIds) record.ItemIds[key] = itemId;
            if (RemoveStale) await ZabbixAgentHostProvisioning.RemoveStaleAsync(ZabbixClient, record, keys, cancellationToken).ConfigureAwait(false);
            return true;
        }, out _, record.HostName))
        {
            return;
        }

        if (!PassThru) return;
        TryRun(cancellationToken => ZabbixAgentHostProvisioning.ReadStatusAsync(ZabbixClient, record.HostId, new HashSet<string>(keys, StringComparer.Ordinal), cancellationToken),
            out ZabbixAgentHostStatus? status, record.HostName);
        if (status is not null) WriteObject(status);
    }
}
