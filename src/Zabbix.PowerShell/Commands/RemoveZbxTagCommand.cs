// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Removes a tag from hosts (<c>host.update</c>).</para>
/// <para type="description">Removes every tag named <c>-Name</c> (or, with <c>-Value</c>, only the one with that value), from the host's current tags — never blind.</para>
/// </summary>
[Cmdlet(VerbsCommon.Remove, "ZbxTag", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High, DefaultParameterSetName = ZabbixTagCommand.ByName)]
[OutputType(typeof(ZabbixHost))]
public sealed class RemoveZbxTagCommand : ZabbixTagCommand
{
    /// <summary>Removes without the confirmation prompt; <c>-WhatIf</c> and an explicit <c>-Confirm</c> still apply.</summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        // Resolved first, then confirmed per host by name. One confirmation, as Add-ZbxTag: a tag is removed, not the
        // host; the second question ShouldContinue adds is for what cannot be undone.
        if (!TryConfirm(Hosts, $"Remove tag {ZabbixTagCommandText.TagList(TagPairs)} from host", Force, out IReadOnlyList<ulong> ids)) return;
        if (!TryRun(async cancellationToken =>
        {
            var parameters = new ZabbixHostGetParameters { HostIds = [.. ids.Distinct()], SelectTags = ZabbixOutput.Extend };
            IReadOnlyList<ZabbixHost> hosts = await ZabbixClient.Hosts.GetAsync(parameters, cancellationToken).ConfigureAwait(false);
            var requests = hosts.Select(host =>
            {
                var tags = (host.Tags ?? [])
                    .Where(tag => !TagPairs.Any(removed => string.Equals(tag.Tag, removed.Name, StringComparison.Ordinal) && (removed.Value is null || string.Equals(tag.Value, removed.Value, StringComparison.Ordinal))))
                    .Select(tag => tag.ToRequest())
                    .ToList();
                return new ZabbixHostUpdateRequest { HostId = host.HostId!.Value, Tags = tags };
            });
            IReadOnlyList<ulong> updated = await ZabbixClient.Hosts.UpdateAsync(requests, cancellationToken).ConfigureAwait(false);
            // The -PassThru re-read selects the tags it just changed.
            return updated.Count == 0 ? [] : await ZabbixClient.Hosts.GetAsync(new ZabbixHostGetParameters { HostIds = updated, SelectTags = ZabbixOutput.Extend }, cancellationToken).ConfigureAwait(false);
        }, out IReadOnlyList<ZabbixHost>? changed, ids)) return;
        if (!PassThru.IsPresent) return;
        foreach (ZabbixHost host in changed) WriteObject(host);
    }
}
