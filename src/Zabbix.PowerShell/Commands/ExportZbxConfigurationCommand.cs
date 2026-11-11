// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Exports hosts, templates and other objects as a document (<c>configuration.export</c>).</para>
/// <para type="description">
/// Name what to export with <c>-HostGroup</c>, <c>-TemplateGroup</c>, <c>-HostReference</c>, <c>-Template</c>, <c>-Map</c>,
/// <c>-MediaType</c>, <c>-Dashboard</c>, <c>-ImageId</c> and <c>-GlobalRegexId</c>. A host group exports with the
/// hosts in it, and a template group with the templates in it, so a group export can be imported as it stands.
/// Hosts and templates are exported <c>-BatchSize</c> at a time (a single large export exhausts the frontend's PHP
/// memory limit) and the answers merge into one document, written to the pipeline as text, or to <c>-Path</c>
/// (replacing the file unless <c>-NoClobber</c>; <c>-WhatIf</c> previews the write), in the format the file's extension
/// names unless <c>-Format</c> names it. Plaintext media-type/IPMI/OAuth
/// secrets in the export are redacted by default, with a warning; pass <c>-Unredacted</c> to keep them, or answer yes
/// to the confirmation prompt in a host that can show one.
/// </para>
/// </summary>
/// <remarks>
/// The redaction and the prompt are <see cref="ZabbixSecretDisclosure"/>'s. <c>configuration.export</c>'s own
/// <c>host_groups</c>/<c>template_groups</c> options export the group objects only
/// (<c>CConfigurationExport::gatherHostGroups</c>/<c>gatherTemplateGroups</c>, 7.0–8.0, read the group rows and
/// nothing else), so the command resolves the members itself (<c>host.get</c>/<c>template.get</c> by
/// <c>groupids</c>) into <c>hosts</c>/<c>templates</c>.
/// </remarks>
[Cmdlet(VerbsData.Export, "ZbxConfiguration", SupportsShouldProcess = true, DefaultParameterSetName = "Pipeline")]
[OutputType(typeof(string))]
public sealed class ExportZbxConfigurationCommand : ZabbixServerCmdlet
{
    /// <summary>
    /// The wire format: <c>json</c>, <c>xml</c> or <c>yaml</c>. When omitted, a file's extension names it (<c>.json</c>,
    /// <c>.xml</c>, <c>.yaml</c>/<c>.yml</c>), else <c>json</c>; given with a file whose extension names another format, it is
    /// refused (<c>ZabbixConfigurationFormatMismatch</c>).
    /// </summary>
    [Parameter]
    public ZabbixConfigurationFormat? Format { get; set; }

    /// <summary>Host groups to export, each with the hosts in it.</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    [Alias("GroupId")]
    [ZabbixReference<ZabbixHostGroup>]
    [ZabbixCompleter<ZabbixHostGroup>]
    public ZabbixReference<ZabbixHostGroup>[]? HostGroup { get; set; }

    /// <summary>Template groups to export, each with the templates in it.</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    [ZabbixReference<ZabbixTemplateGroup>]
    [ZabbixCompleter<ZabbixTemplateGroup>]
    public ZabbixReference<ZabbixTemplateGroup>[]? TemplateGroup { get; set; }

    /// <summary>Hosts to export by name.</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    [Alias("Host", "HostId")]
    [ZabbixReference<ZabbixHost>]
    [ZabbixCompleter<ZabbixHost>]
    public ZabbixReference<ZabbixHost>[]? HostReference { get; set; }

    /// <summary>Templates to export by name.</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    [Alias("TemplateId")]
    [ZabbixReference<ZabbixTemplate>]
    [ZabbixCompleter<ZabbixTemplate>]
    public ZabbixReference<ZabbixTemplate>[]? Template { get; set; }

    /// <summary>Network maps to export.</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    [Alias("MapId")]
    [ZabbixReference<ZabbixMap>]
    [ZabbixCompleter<ZabbixMap>]
    public ZabbixReference<ZabbixMap>[]? Map { get; set; }

    /// <summary>Media types to export.</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    [Alias("MediaTypeId")]
    [ZabbixReference<ZabbixMediaType>]
    [ZabbixCompleter<ZabbixMediaType>]
    public ZabbixReference<ZabbixMediaType>[]? MediaType { get; set; }

    /// <summary>Dashboards to export (8.0+; <c>ZabbixConfigurationExportOptionRequest.Dashboards</c>).</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    [Alias("DashboardId")]
    [ZabbixReference<ZabbixDashboard>]
    [ZabbixCompleter<ZabbixDashboard>]
    public ZabbixReference<ZabbixDashboard>[]? Dashboard { get; set; }

    /// <summary>Image ids to export; no name-lookup entity is registered for images, so this takes ids only.</summary>
    [Parameter]
    public ulong[]? ImageId { get; set; }

    /// <summary>Global regex ids to export (8.0+); no name-lookup entity is registered for these, so this takes ids only.</summary>
    [Parameter]
    public ulong[]? GlobalRegexId { get; set; }

    /// <summary>
    /// Hosts and templates per <c>configuration.export</c> call (default 20); the documents merge into one. Lower it if
    /// the server's PHP memory limit is tight.
    /// </summary>
    [Parameter]
    [ValidateRange(1, 1000)]
    public int BatchSize { get; set; } = ExportZbxConfigurationCommand.DefaultBatchSize;

    /// <summary>
    /// Writes the export to this file instead of the pipeline; replaced unless <c>-NoClobber</c>. Wildcards that match
    /// one existing file name it; otherwise the path is the file's name as written.
    /// </summary>
    [Parameter(ParameterSetName = "Path", Mandatory = true)]
    [SupportsWildcards]
    [ValidateNotNullOrEmpty]
    public string? Path { get; set; }

    /// <summary>Writes the export to this file instead of the pipeline, taken literally (no wildcards); replaced unless <c>-NoClobber</c>.</summary>
    [Parameter(ParameterSetName = "LiteralPath", Mandatory = true)]
    [Alias("PSPath", "LP")]
    [ValidateNotNullOrEmpty]
    public string? LiteralPath { get; set; }

    /// <summary>Leaves a file that already exists at <c>-Path</c> or <c>-LiteralPath</c> as it is (an error) rather than replacing it.</summary>
    [Parameter(ParameterSetName = "Path")]
    [Parameter(ParameterSetName = "LiteralPath")]
    public SwitchParameter NoClobber { get; set; }

    /// <summary>Includes plaintext media-type/IPMI/OAuth secrets in the export rather than redacting them, without asking.</summary>
    /// <remarks>
    /// This has no <c>-Force</c> alias on purpose: <c>-Force</c> reads as "overwrite the file", and as an alias of this
    /// it would write plaintext secrets to disk.
    /// </remarks>
    [Parameter]
    public SwitchParameter Unredacted { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        string? file = null;
        ZabbixConfigurationFormat? format = Format;
        if ((Path ?? LiteralPath) is { } written)
        {
            if (ZabbixDocumentFile.ResolveOutput(this, written, literal: Path is null, out string resolved) is { } ambiguous)
            {
                WriteError(ambiguous);
                return;
            }
            if (ZabbixConfigurationFormatWire.Resolve(Format, resolved, out format) is { } mismatch)
            {
                WriteError(mismatch);
                return;
            }
            if (!ShouldProcess(resolved, "Write configuration export")) return;
            file = resolved;
        }
        string wire = (format ?? ZabbixConfigurationFormat.Json).ToWire();
        if (!TryRun(async cancellationToken =>
        {
            IReadOnlyList<ulong>? hostGroups = HostGroup is null ? null : [.. (await ResolveAsync(HostGroup, cancellationToken).ConfigureAwait(false))];
            IReadOnlyList<ulong>? templateGroups = TemplateGroup is null ? null : [.. (await ResolveAsync(TemplateGroup, cancellationToken).ConfigureAwait(false))];
            IReadOnlyList<ulong>? hosts = HostReference is null ? null : [.. (await ResolveAsync(HostReference, cancellationToken).ConfigureAwait(false))];
            IReadOnlyList<ulong>? templates = Template is null ? null : [.. (await ResolveAsync(Template, cancellationToken).ConfigureAwait(false))];
            var options = new ZabbixConfigurationExportOptionRequest
            {
                HostGroups = hostGroups,
                TemplateGroups = templateGroups,
                Hosts = Union(hosts, await HostsInAsync(ZabbixClient, hostGroups, cancellationToken).ConfigureAwait(false)),
                Templates = Union(templates, await TemplatesInAsync(ZabbixClient, templateGroups, cancellationToken).ConfigureAwait(false)),
                Maps = Map is null ? null : [.. (await ResolveAsync(Map, cancellationToken).ConfigureAwait(false))],
                MediaTypes = MediaType is null ? null : [.. (await ResolveAsync(MediaType, cancellationToken).ConfigureAwait(false))],
                Dashboards = Dashboard is null ? null : [.. (await ResolveAsync(Dashboard, cancellationToken).ConfigureAwait(false))],
                Images = ImageId,
                GlobalRegexes = GlobalRegexId,
            };
            return await ExportAsync(ZabbixClient, wire, options, BatchSize, cancellationToken).ConfigureAwait(false);
        }, out string? text, Connected.Name))
        {
            return;
        }

        Emit(this, text!, wire, file, Unredacted, NoClobber);
    }

    /// <summary>
    /// How many hosts and templates one <c>configuration.export</c> call carries by default. Against the stock
    /// frontend's 128M PHP <c>memory_limit</c>, the stock templates 50 at a time export on 7.0.31 and 8.0.0rc1 (2.9/3.2 MB of JSON), 100 at a time exhaust it on
    /// 7.0.31 (HTTP 500, "Allowed memory size … exhausted" in <c>CConfigurationExport.php</c>), all of them at once on both.
    /// </summary>
    internal const int DefaultBatchSize = 20;

    /// <summary>
    /// Exports <paramref name="options"/>, its hosts and templates <paramref name="batchSize"/> at a time: the first
    /// call carries every other option too, and the answers merge into one document
    /// (<see cref="ZabbixConfigurationDocument"/>).
    /// </summary>
    internal static async Task<string> ExportAsync(ZabbixClient client, string format, ZabbixConfigurationExportOptionRequest options, int batchSize, CancellationToken cancellationToken)
    {
        IReadOnlyList<ulong> templates = options.Templates ?? [];
        IReadOnlyList<ulong> hosts = options.Hosts ?? [];
        if (templates.Count + hosts.Count <= batchSize)
            return await client.Configuration.ExportAsync(new ZabbixConfigurationExportRequest { Format = format, Options = options }, cancellationToken).ConfigureAwait(false);

        var documents = new List<string>();
        IEnumerable<(bool Template, ulong Id)> members = templates.Select(id => (true, id)).Concat(hosts.Select(id => (false, id)));
        foreach ((bool Template, ulong Id)[] batch in members.Chunk(batchSize))
        {
            ulong[] batchTemplates = [.. batch.Where(member => member.Template).Select(member => member.Id)];
            ulong[] batchHosts = [.. batch.Where(member => !member.Template).Select(member => member.Id)];
            ZabbixConfigurationExportOptionRequest part = documents.Count == 0
                ? options with { Templates = batchTemplates.Length == 0 ? null : batchTemplates, Hosts = batchHosts.Length == 0 ? null : batchHosts }
                : new ZabbixConfigurationExportOptionRequest { Templates = batchTemplates.Length == 0 ? null : batchTemplates, Hosts = batchHosts.Length == 0 ? null : batchHosts };
            documents.Add(await client.Configuration.ExportAsync(new ZabbixConfigurationExportRequest { Format = format, Options = part }, cancellationToken).ConfigureAwait(false));
        }
        return ZabbixConfigurationDocument.Merge(documents, format).ToText();
    }

    /// <summary>The ids of the hosts in <paramref name="groupIds"/> (<c>host.get groupids</c>); none asked for, no call.</summary>
    internal static async Task<IReadOnlyList<ulong>> HostsInAsync(ZabbixClient client, IReadOnlyList<ulong>? groupIds, CancellationToken cancellationToken)
    {
        if (groupIds is not { Count: > 0 }) return [];
        IReadOnlyList<ZabbixHost> hosts = await client.Hosts.GetAsync(
            new ZabbixHostGetParameters { GroupIds = groupIds, Output = s_hostIdField }, cancellationToken).ConfigureAwait(false);
        return [.. hosts.Select(host => host.HostId!.Value)];
    }

    /// <summary>The ids of the templates in <paramref name="groupIds"/> (<c>template.get groupids</c>); none asked for, no call.</summary>
    internal static async Task<IReadOnlyList<ulong>> TemplatesInAsync(ZabbixClient client, IReadOnlyList<ulong>? groupIds, CancellationToken cancellationToken)
    {
        if (groupIds is not { Count: > 0 }) return [];
        IReadOnlyList<ZabbixTemplate> templates = await client.Templates.GetAsync(
            new ZabbixTemplateGetParameters { GroupIds = groupIds, Output = s_templateIdField }, cancellationToken).ConfigureAwait(false);
        return [.. templates.Select(template => template.TemplateId!.Value)];
    }

    private static readonly ZabbixOutput s_hostIdField = ZabbixOutput.Of("hostid");
    private static readonly ZabbixOutput s_templateIdField = ZabbixOutput.Of("templateid");

    /// <summary>The named ids and the groups' members, each once; <see langword="null"/> (the option omitted) when both are empty.</summary>
    internal static IReadOnlyList<ulong>? Union(IReadOnlyList<ulong>? named, IReadOnlyList<ulong> members)
        => (named ?? []).Union(members).ToList() is { Count: > 0 } all ? all : named;

    /// <summary>Gates the secrets, then writes to the pipeline or a file (<see cref="ZabbixDocumentFile"/>: a failure is an error record).</summary>
    internal static void Emit(PSCmdlet cmdlet, string text, string format, string? path, bool includeSecrets, bool noClobber)
    {
        (string redacted, int count) = ZabbixConfigurationSecrets.Redact(text, format);
        string output = ZabbixSecretDisclosure.ShouldDisclose(cmdlet, count, includeSecrets, "This export") ? text : redacted;
        if (path is null) cmdlet.WriteObject(output);
        else if (ZabbixDocumentFile.Write(cmdlet, path, output, noClobber) is { } failed) cmdlet.WriteError(failed);
    }
}
