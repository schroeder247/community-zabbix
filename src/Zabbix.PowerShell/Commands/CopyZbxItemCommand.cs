// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Copies items from one host onto other hosts.</para>
/// <para type="description">Exports the source host, keeps only the items with the named keys (every item when <c>-Key</c>
/// is omitted; a key the source does not have is an error), and imports them (<c>createMissing</c>) onto each target
/// host, one target at a time. Writes nothing unless <c>-PassThru</c>. Supports <c>-WhatIf</c>/<c>-Confirm</c> (ConfirmImpact
/// Medium), once per resolved target host.</para>
/// </summary>
[Cmdlet(VerbsCommon.Copy, "ZbxItem", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium)]
[OutputType(typeof(ZabbixItem))]
public sealed class CopyZbxItemCommand : ZabbixServerCmdlet
{
    /// <summary>The source host the items are copied from.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    [ZabbixReference<ZabbixHost>]
    [ZabbixCompleter<ZabbixHost>]
    public ZabbixReference<ZabbixHost>? SourceHost { get; set; }

    /// <summary>The item keys to copy; every item on <c>-SourceHost</c> when omitted.</summary>
    [Parameter]
    public string[]? Key { get; set; }

    /// <summary>The hosts to copy the items onto.</summary>
    [Parameter(Mandatory = true, Position = 1, ValueFromPipeline = true)]
    [ZabbixReference<ZabbixHost>]
    [ZabbixCompleter<ZabbixHost>]
    public ZabbixReference<ZabbixHost>[]? TargetHost { get; set; }

    /// <summary>
    /// Updates items with the same key already on a target host from the source; without it, those are left as they
    /// are and only missing items are created.
    /// </summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    /// <summary>Writes the copied items, read back from the target hosts; without it the command writes nothing.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    private readonly List<ZabbixReference<ZabbixHost>> _targets = [];

    /// <inheritdoc/>
    protected override void ProcessRecord() => _targets.AddRange(TargetHost!);

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (_targets.Count == 0) return;
        // Resolved before the prompt, so -WhatIf and -Confirm name each target host; a name that matches nothing is its
        // own ObjectNotFound record, written by the resolution.
        if (!TryRun(cancellationToken => ResolveTargetsAsync([SourceHost!], cancellationToken), out IReadOnlyList<(ulong Id, string Target)>? source, SourceHost)) return;
        if (source.Count == 0) return;
        if (!TryRun(cancellationToken => ResolveTargetsAsync(_targets, cancellationToken), out IReadOnlyList<(ulong Id, string Target)>? targets, _targets.ToArray())) return;
        IReadOnlyList<ulong> confirmed = Confirmed(targets, $"Copy items from {source[0].Target}", force: false);
        if (confirmed.Count == 0) return;
        if (TryRun(cancellationToken => CopyAsync(source[0].Id, confirmed, cancellationToken), out IReadOnlyList<ZabbixItem>? copied, _targets.ToArray()))
        {
            foreach (ZabbixItem item in copied) WriteObject(item);
        }
    }

    private async Task<IReadOnlyList<ZabbixItem>> CopyAsync(ulong sourceId, IReadOnlyList<ulong> targetIds, CancellationToken cancellationToken)
    {
        var itemParameters = new ZabbixItemGetParameters
        {
            HostIds = [sourceId],
            Filter = Key is { Length: > 0 } ? new() { ["key_"] = Key } : null,
        };
        IReadOnlyList<ZabbixItem> items = await ListAsync(ZabbixClient.Items.GetAsync(itemParameters, cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (string missing in (Key ?? []).Where(key => !items.Any(item => string.Equals(item.Key?.ToString(), key, StringComparison.Ordinal))))
        {
            ZabbixPipeline.Current?.Post(new ErrorWrite(new ErrorRecord(new ItemNotFoundException($"{SourceHost} has no item with the key '{missing}'"),
                "ZabbixItemNotFound", ErrorCategory.ObjectNotFound, missing)));
        }
        if (items.Count == 0) return [];

        IReadOnlyList<ZabbixHost> targets = await ListAsync(ZabbixClient.Hosts.GetAsync(new ZabbixHostGetParameters { HostIds = [.. targetIds] }, cancellationToken), cancellationToken).ConfigureAwait(false);
        string document = await ZabbixClient.Configuration.ExportAsync(
            new ZabbixConfigurationExportRequest { Format = "json", Options = new ZabbixConfigurationExportOptionRequest { Hosts = [sourceId] } },
            cancellationToken).ConfigureAwait(false);
        // Only the items asked for, not the whole host's items: configuration.export has no per-item scope, so the host's document is narrowed here,
        // by the export's own "key" member, before it is imported.
        JsonNode exported = JsonNode.Parse(document) ?? throw new JsonException("configuration.export answered no document");
        JsonObject host = exported["zabbix_export"]?["hosts"]?.AsArray().OfType<JsonObject>().SingleOrDefault()
            ?? throw new JsonException($"configuration.export answered no host for {SourceHost}");
        if (Key is { Length: > 0 } && host["items"] is JsonArray exportedItems)
        {
            foreach (JsonNode? item in exportedItems.ToArray())
                if (item?["key"]?.GetValue<string>() is not { } key || !Key.Contains(key, StringComparer.Ordinal)) exportedItems.Remove(item);
        }

        foreach (ZabbixHost target in targets)
        {
            // Retargeted structurally, the host's own "host" and "name" members (a text replace would also rewrite any
            // value that happens to equal the source's name).
            host["host"] = target.HostName;
            host["name"] = target.VisibleName ?? target.HostName;
            var rules = new ZabbixConfigurationImportRuleRequest
            {
                Hosts = new ZabbixConfigurationImportRuleHostRequest { CreateMissing = false, UpdateExisting = false },
                Items = new ZabbixConfigurationImportRuleItemRequest { CreateMissing = true, UpdateExisting = Force.IsPresent },
            };
            await ZabbixClient.Configuration.ImportAsync(new ZabbixConfigurationImportRequest { Format = "json", Source = exported.ToJsonString(), Rules = rules }, cancellationToken).ConfigureAwait(false);
        }
        if (!PassThru) return [];
        var copied = new ZabbixItemGetParameters
        {
            HostIds = [.. targets.Select(target => target.HostId!.Value)],
            Filter = new() { ["key_"] = items.Select(item => item.Key.ToString()!).ToArray() },
        };
        return await ListAsync(ZabbixClient.Items.GetAsync(copied, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
}
