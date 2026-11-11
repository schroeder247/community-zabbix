// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Adds a tag to hosts, merged with their current tags (<c>host.update</c>).</para>
/// <para type="description">
/// The API replaces a host's whole tag set on update, so this reads each host's current tags first and sends them back
/// with the new tags added; existing tags are never lost, and a tag with the same name and value already on the host
/// is kept once. Works on hosts only. Every tag given or piped in is applied to every host, in one
/// <c>host.update</c> call. Writes nothing unless <c>-PassThru</c> is given. Supports <c>-WhatIf</c>/<c>-Confirm</c>
/// (ConfirmImpact Medium).
/// </para>
/// </summary>
/// <remarks>
/// A tag update replaces the whole set (<c>defines.inc.php</c>'s API contract), so this reads each host's current tags
/// first and sends them back with the new one added — never blind. Scoped to hosts.
/// </remarks>
[Cmdlet(VerbsCommon.Add, "ZbxTag", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium, DefaultParameterSetName = ZabbixTagCommand.ByName)]
[OutputType(typeof(ZabbixHost))]
public sealed class AddZbxTagCommand : ZabbixTagCommand
{
    /// <summary>Adds without the confirmation prompt a lowered <c>$ConfirmPreference</c> would raise; <c>-WhatIf</c> and an explicit <c>-Confirm</c> still apply.</summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        // Resolved first, then confirmed per host by name (ConfirmImpact Medium): adding a tag merges, it removes
        // nothing, so there is no second ShouldContinue gate.
        if (!TryConfirm(Hosts, $"Add tag {ZabbixTagCommandText.TagList(TagPairs)} to host", Force, out IReadOnlyList<ulong> ids)) return;
        if (!TryRun(async cancellationToken =>
        {
            var parameters = new ZabbixHostGetParameters { HostIds = [.. ids.Distinct()], SelectTags = ZabbixOutput.Extend };
            IReadOnlyList<ZabbixHost> hosts = await ZabbixClient.Hosts.GetAsync(parameters, cancellationToken).ConfigureAwait(false);
            var requests = hosts.Select(host =>
            {
                var tags = (host.Tags ?? []).Select(tag => tag.ToRequest()).ToList();
                // host.update refuses a repeated (tag, value) pair (CHost.php:2600 makes the pair unique in the tags rule):
                // a pair the host already carries is kept once.
                foreach ((string name, string? value) in TagPairs)
                {
                    var tag = new ZabbixTagRequest { Tag = name, Value = value ?? string.Empty };
                    if (!tags.Contains(tag)) tags.Add(tag);
                }
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
