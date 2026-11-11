// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Copies a host to a new technical name on the same server.</para>
/// <para type="description">
/// Exports the host (<c>configuration.export</c>), sets its technical and visible name to <c>-NewName</c>, points
/// the document's references to the host (graph items, trigger expressions) at the copy, and imports the result
/// (<c>configuration.import</c>, creating what is missing): items, triggers, graphs, discovery rules, macros, tags and
/// interfaces come along. A calculated item's formula that names the host is not rewritten. With <c>-Force</c>, a
/// host already named <c>-NewName</c> (and its items, triggers, graphs and discovery rules) is updated from the copy;
/// without it, existing objects are left as they are. Writes nothing unless <c>-PassThru</c>. Supports
/// <c>-WhatIf</c>/<c>-Confirm</c> (ConfirmImpact Medium), naming the resolved host.
/// </para>
/// </summary>
[Cmdlet(VerbsCommon.Copy, "ZbxHost", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium)]
[OutputType(typeof(ZabbixHost))]
public sealed class CopyZbxHostCommand : ZabbixServerCmdlet
{
    /// <summary>The host to copy: an id, a name, or an object from Get-ZbxHost.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("Id")]
    [ZabbixReference<ZabbixHost>]
    [ZabbixCompleter<ZabbixHost>]
    public ZabbixReference<ZabbixHost>? InputObject { get; set; }

    /// <summary>The technical name for the copy.</summary>
    [Parameter(Mandatory = true, Position = 1)]
    [ValidateNotNullOrWhiteSpace]
    public string? NewName { get; set; }

    /// <summary>
    /// Updates a host that already has the new name (and its items, triggers, graphs and discovery rules) from the
    /// copy; without it, only what is missing is created.
    /// </summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    /// <summary>Writes the copy, read back by its new name; without it the command writes nothing.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        // Resolved before the prompt, so -WhatIf and -Confirm name the host ('host 'x' (id)'); a name that matches
        // nothing is its own ObjectNotFound record, written by the resolution, and nothing is copied.
        if (!TryRun(cancellationToken => ResolveTargetsAsync([InputObject!], cancellationToken), out IReadOnlyList<(ulong Id, string Target)>? resolved, InputObject)) return;
        if (resolved.Count == 0) return;
        (ulong id, string target) = resolved[0];
        if (!ShouldProcess($"{target} -> '{NewName}'", "Copy host")) return;
        if (TryRun(cancellationToken => CopyAsync(id, cancellationToken), out IReadOnlyList<ZabbixHost>? copies, InputObject) && PassThru)
        {
            foreach (ZabbixHost copy in copies) WriteObject(copy);
        }
    }

    private async Task<IReadOnlyList<ZabbixHost>> CopyAsync(ulong id, CancellationToken cancellationToken)
    {
        ZabbixHost source = (await ListAsync(ZabbixClient.Hosts.GetAsync(new ZabbixHostGetParameters { HostIds = [id] }, cancellationToken), cancellationToken).ConfigureAwait(false)) is [{ } found, ..]
            ? found
            : throw new ItemNotFoundException($"host {id} does not exist on {Connected.Name}");
        string document = await ZabbixClient.Configuration.ExportAsync(
            new ZabbixConfigurationExportRequest { Format = "json", Options = new ZabbixConfigurationExportOptionRequest { Hosts = [id] } },
            cancellationToken).ConfigureAwait(false);
        string renamed = ZabbixExportRename.Rename(document, "hosts", "host", source.TechnicalName!, NewName!);
        var rules = new ZabbixConfigurationImportRuleRequest
        {
            Hosts = new ZabbixConfigurationImportRuleHostRequest { CreateMissing = true, UpdateExisting = Force.IsPresent },
            Items = new ZabbixConfigurationImportRuleItemRequest { CreateMissing = true, UpdateExisting = Force.IsPresent },
            Triggers = new ZabbixConfigurationImportRuleTriggerRequest { CreateMissing = true, UpdateExisting = Force.IsPresent },
            Graphs = new ZabbixConfigurationImportRuleGraphRequest { CreateMissing = true, UpdateExisting = Force.IsPresent },
            DiscoveryRules = new ZabbixConfigurationImportRuleDiscoveryRuleRequest { CreateMissing = true, UpdateExisting = Force.IsPresent },
            ValueMaps = new ZabbixConfigurationImportRuleValueMapRequest { CreateMissing = true, UpdateExisting = Force.IsPresent },
            HttpTests = new ZabbixConfigurationImportRuleHttpTestRequest { CreateMissing = true, UpdateExisting = Force.IsPresent },
            TemplateLinkage = new ZabbixConfigurationImportRuleTemplateLinkageRequest { CreateMissing = true },
        };
        await ZabbixClient.Configuration.ImportAsync(new ZabbixConfigurationImportRequest { Format = "json", Source = renamed, Rules = rules }, cancellationToken).ConfigureAwait(false);
        if (!PassThru) return [];
        var byName = new ZabbixHostGetParameters { Filter = new() { ["host"] = NewName! } };
        return await ListAsync(ZabbixClient.Hosts.GetAsync(byName, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
}
