// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Exports the host <c>Initialize-ZbxAgentHost</c> provisioned for a running agent session, with
/// its interface and items, via <c>configuration.export</c>.</para>
/// <para type="description">
/// The host must already have been provisioned on the session by <c>Initialize-ZbxAgentHost</c>. The export is a
/// host export (its <c>hosts</c> section), not a <c>templates</c> document, exactly as the server would import it
/// back with <c>Import-ZbxConfiguration</c>. <c>-Format</c> is <c>Json</c> (default), <c>Xml</c> or <c>Yaml</c>; the
/// text is written to the pipeline, or to <c>-Path</c>. Plaintext secrets are redacted unless <c>-Unredacted</c> is
/// given.
/// </para>
/// </summary>
/// <remarks>
/// The document is asked of the server once the host exists, through the same <c>configuration.export</c> call
/// <c>Export-ZbxConfiguration</c>/<c>Copy-ZbxHost</c> use, scoped to the one host; it is never shaped client-side, so
/// there is no separate document model to keep in sync with the server's.
/// </remarks>
[Cmdlet(VerbsData.Export, "ZbxAgentTemplate", SupportsShouldProcess = true, DefaultParameterSetName = "Pipeline")]
[OutputType(typeof(string))]
public sealed class ExportZbxAgentTemplateCommand : ZabbixAgentHostCommand
{
    /// <summary>Export format: <c>Json</c> (default), <c>Xml</c> or <c>Yaml</c>.</summary>
    [Parameter]
    public ZabbixConfigurationFormat Format { get; set; } = ZabbixConfigurationFormat.Json;

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

    /// <summary>Includes plaintext media-type/IPMI/OAuth secrets in the export rather than redacting them.</summary>
    [Parameter]
    public SwitchParameter Unredacted { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (!TryGetSession(out ZabbixAgentSession session)) return;
        if (!TryGetRecord(session, out ZabbixAgentHostRecord? record)) return;
        string? file = null;
        if ((Path ?? LiteralPath) is { } written)
        {
            if (ZabbixDocumentFile.ResolveOutput(this, written, literal: Path is null, out string resolved) is { } ambiguous)
            {
                WriteError(ambiguous);
                return;
            }
            if (!ShouldProcess(resolved, "Write agent template export")) return;
            file = resolved;
        }

        if (!TryRun(cancellationToken => ZabbixClient.Configuration.ExportAsync(
            new ZabbixConfigurationExportRequest { Format = Format.ToWire(), Options = new ZabbixConfigurationExportOptionRequest { Hosts = [record.HostId] } },
            cancellationToken), out string? text, record.HostName))
        {
            return;
        }

        ExportZbxConfigurationCommand.Emit(this, text!, Format.ToWire(), file, Unredacted, NoClobber);
    }
}
