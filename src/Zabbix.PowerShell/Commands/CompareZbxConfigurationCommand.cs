// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Previews what <c>Import-ZbxConfiguration</c> would change, without applying it (<c>configuration.importcompare</c>).</para>
/// <para type="description">The diff's "before" side carries the live server's current credentials for updated
/// objects, so a diff with plaintext secrets is gated the same way <c>Export-ZbxConfiguration</c> is.</para>
/// </summary>
[Cmdlet(VerbsData.Compare, "ZbxConfiguration", DefaultParameterSetName = ParameterSets.Path)]
[OutputType("Community.Zabbix.PowerShell.ConfigurationDifference")]
public sealed class CompareZbxConfigurationCommand : ZabbixServerCmdlet
{
    private static class ParameterSets
    {
        public const string Text = "Text";
        public const string Path = "Path";
        public const string LiteralPath = "LiteralPath";
    }

    /// <summary>The document text to compare against the live server.</summary>
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

    /// <summary>The same typed rules <c>Import-ZbxConfiguration -Rules</c> takes.</summary>
    [Parameter(Mandatory = true)]
    public ZabbixConfigurationImportRuleRequest? Rules { get; set; }

    /// <summary>Includes plaintext credentials the diff's "before" side carries, rather than redacting them.</summary>
    [Parameter]
    public SwitchParameter Unredacted { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (ParameterSetName == ParameterSets.Text)
        {
            Compare(Source!, "<pipeline document>", Format);
            return;
        }
        foreach ((string file, string text, ErrorRecord? unreadable) in ZabbixDocumentFile.ReadAll(this, Path ?? LiteralPath!, literal: Path is null))
        {
            if (unreadable is not null) WriteError(unreadable);
            else if (ZabbixConfigurationFormatWire.Resolve(Format, file, out ZabbixConfigurationFormat? format) is { } mismatch) WriteError(mismatch);
            else Compare(text, file, format);
        }
    }

    // One document's difference from the live server.
    private void Compare(string text, string target, ZabbixConfigurationFormat? format)
    {
        if (!TryRun(cancellationToken => ZabbixClient.Configuration.ImportCompareAsync(
            new ZabbixConfigurationImportRequest { Format = format?.ToWire() ?? TestZbxConfigurationCommand.DetectFormat(text), Source = text, Rules = Rules! }, cancellationToken), out JsonElement diff, target))
        {
            return;
        }

        string raw = diff.GetRawText();
        (string redacted, int count) = ZabbixConfigurationSecrets.Redact(raw, "json");
        // The diff as ConvertFrom-Json shapes it (navigable: $diff.templates.added), enumerated — an
        // unchanged document's [] writes nothing, not one JsonElement.
        JsonElement shown = ZabbixSecretDisclosure.ShouldDisclose(this, count, Unredacted, "This diff")
            ? diff
            : JsonSerializer.Deserialize<JsonElement>(redacted);
        WriteObject(ZabbixJsonShape.ToShell(shown, "Community.Zabbix.PowerShell.ConfigurationDifference"), enumerateCollection: true);
    }
}
