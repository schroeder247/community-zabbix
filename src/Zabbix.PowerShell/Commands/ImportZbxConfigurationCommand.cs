// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Applies a configuration document to the server (<c>configuration.import</c>).</para>
/// </summary>
[Cmdlet(VerbsData.Import, "ZbxConfiguration", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High, DefaultParameterSetName = ParameterSets.Path)]
public sealed class ImportZbxConfigurationCommand : ZabbixServerCmdlet
{
    private static class ParameterSets
    {
        public const string Text = "Text";
        public const string Path = "Path";
        public const string LiteralPath = "LiteralPath";
    }

    /// <summary>The document text.</summary>
    [Parameter(ParameterSetName = ParameterSets.Text, Mandatory = true, ValueFromPipeline = true)]
    [ValidateNotNullOrEmpty]
    public string? Source { get; set; }

    /// <summary>
    /// Paths of document files; PowerShell wildcards (<c>*.json</c>) are expanded, and each file is its own document.
    /// </summary>
    [Parameter(ParameterSetName = ParameterSets.Path, Mandatory = true, Position = 0)]
    [SupportsWildcards]
    [ValidateNotNullOrEmpty]
    public string[]? Path { get; set; }

    /// <summary>Paths of document files, taken literally (no wildcards); a piped file (<c>Get-ChildItem *.json</c>) binds by its <c>PSPath</c>.</summary>
    [Parameter(ParameterSetName = ParameterSets.LiteralPath, Mandatory = true, ValueFromPipelineByPropertyName = true)]
    [Alias("PSPath", "LP")]
    [ValidateNotNullOrEmpty]
    public string[]? LiteralPath { get; set; }

    /// <summary>
    /// The document's wire format: <c>json</c>, <c>xml</c> or <c>yaml</c>. When omitted, a file's extension names it
    /// (<c>.json</c>, <c>.xml</c>, <c>.yaml</c>/<c>.yml</c>), else the text's first character does (<c>{</c> JSON,
    /// <c>&lt;</c> XML, otherwise YAML); a file whose extension names another format than <c>-Format</c> is refused
    /// (<c>ZabbixConfigurationFormatMismatch</c>) and the other files go on.
    /// </summary>
    [Parameter]
    public ZabbixConfigurationFormat? Format { get; set; }

    /// <summary>What each object family does when the document names an object that exists, or does not (the typed <c>rules</c> of <c>configuration.import</c>).</summary>
    [Parameter(Mandatory = true)]
    public ZabbixConfigurationImportRuleRequest? Rules { get; set; }

    /// <summary>Skips the confirmations, the second one a rule that deletes asks for included; <c>-WhatIf</c> and an explicit <c>-Confirm</c> still apply.</summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    private bool _yesToAll;
    private bool _noToAll;

    /// <inheritdoc/>
    /// <remarks>
    /// A sink, as a changing cmdlet is (Learn, "Support the PassThru Parameter"): <c>configuration.import</c> answers
    /// nothing a script reads unless it asks for the missing objects, which the cmdlet doesn't. One
    /// confirmation per document (<c>ConfirmImpact.High</c>: an import rewrites objects wholesale), the second question
    /// (<c>ShouldContinue</c>) only when a rule deletes what the document does not name (<c>deleteMissing</c>), so an
    /// ordinary import asks once.
    /// </remarks>
    protected override void ProcessRecord()
    {
        if (ParameterSetName == ParameterSets.Text)
        {
            Import(Source!, "<pipeline document>", Format);
            return;
        }
        foreach ((string file, string text, ErrorRecord? unreadable) in ZabbixDocumentFile.ReadAll(this, Path ?? LiteralPath!, literal: Path is null))
        {
            if (unreadable is not null) WriteError(unreadable);
            else if (ZabbixConfigurationFormatWire.Resolve(Format, file, out ZabbixConfigurationFormat? format) is { } mismatch) WriteError(mismatch);
            else Import(text, file, format);
        }
    }

    // One document: confirmed, then imported.
    private void Import(string text, string target, ZabbixConfigurationFormat? format)
    {
        bool deletes = ZabbixJsonShape.FromShell(Rules).GetRawText().Contains("\"deleteMissing\":true", StringComparison.Ordinal);
        if (deletes
            ? !ShouldChange(target, "Import configuration, deleting what the document does not name", Force, ref _yesToAll, ref _noToAll)
            : !ShouldProcessUnlessForced(target, "Import configuration", Force))
        {
            return;
        }

        TryRun(cancellationToken => ZabbixClient.Configuration.ImportAsync(
            new ZabbixConfigurationImportRequest { Format = format?.ToWire() ?? TestZbxConfigurationCommand.DetectFormat(text), Source = text, Rules = Rules! }, cancellationToken), out ZabbixConfigurationImportResult? _, target);
    }
}
