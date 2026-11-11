// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Validates a configuration document client-side (well-formed, and free of plaintext secrets to report) without touching a server.</para>
/// </summary>
[Cmdlet(VerbsDiagnostic.Test, "ZbxConfiguration", DefaultParameterSetName = ParameterSets.Path)]
[OutputType(typeof(ZabbixConfigurationTestResult))]
[OutputType(typeof(bool))]
public sealed class TestZbxConfigurationCommand : ZabbixCmdlet
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
    /// The document's wire format; when omitted, a file's extension names it (<c>.json</c>, <c>.xml</c>,
    /// <c>.yaml</c>/<c>.yml</c>), else the content does (<c>{</c> is JSON, <c>&lt;</c> is XML, else YAML). A file whose
    /// extension names another format than <c>-Format</c> is reported as an issue.
    /// </summary>
    [Parameter]
    public ZabbixConfigurationFormat? Format { get; set; }

    /// <summary>Emits only <see langword="true"/>/<see langword="false"/>.</summary>
    [Parameter]
    public SwitchParameter Quiet { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (ParameterSetName == ParameterSets.Text)
        {
            Test("<pipeline>", Source!, unreadable: null);
            return;
        }
        // A Test- writes no error: a file it cannot find or read is its finding.
        foreach ((string file, string text, ErrorRecord? unreadable) in ZabbixDocumentFile.ReadAll(this, Path ?? LiteralPath!, literal: Path is null))
            Test(file, text, unreadable);
    }

    // One document's findings.
    private void Test(string origin, string text, ErrorRecord? unreadable)
    {
        var issues = new List<string>();
        if (unreadable is not null) issues.Add($"Read failed: {unreadable.Exception.Message}");
        // A file's extension names its format when -Format does not; the two disagreeing is a finding.
        if (ZabbixConfigurationFormatWire.Resolve(Format, origin, out ZabbixConfigurationFormat? named) is { } mismatch) issues.Add(mismatch.Exception.Message);
        string format = named?.ToWire() ?? DetectFormat(text);
        if (issues.Count == 0)
        {
            try
            {
                // Every format must carry the zabbix_export root — CXmlValidator.php:43 (7.0) makes it
                // XML_REQUIRED for whatever the reader parsed, JSON, XML or YAML alike.
                bool rooted;
                switch (format)
                {
                    case "xml":
                        rooted = System.Xml.Linq.XDocument.Parse(text).Root?.Name.LocalName == ExportRoot;
                        break;
                    case "yaml":
                        rooted = YamlHasExportRoot(text);
                        break;
                    default:
                        using (JsonDocument document = JsonDocument.Parse(text))
                            rooted = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(ExportRoot, out _);
                        break;
                }
                if (!rooted) issues.Add($"The document has no '{ExportRoot}' root: it is not a Zabbix configuration export.");
            }
            catch (Exception failure) when (failure is not PipelineStoppedException)
            {
                issues.Add($"Parse failed: {failure.Message}");
            }
        }

        var result = new ZabbixConfigurationTestResult
        {
            IsValid = issues.Count == 0,
            Source = origin,
            Format = format,
            PlaintextSecretCount = ZabbixConfigurationSecrets.Count(text, format),
            Issues = issues,
        };
        WriteObject(Quiet.IsPresent ? result.IsValid : result);
    }

    internal const string ExportRoot = "zabbix_export";

    // The module carries no YAML parser (YamlDotNet lives in Zabbix.Configuration.Yaml, which the module does not
    // reference); the root check needs only the first mapping key at column 0 — past blank lines, comments, a
    // %directive and the "---" document marker — which is how every Zabbix YAML export begins ("zabbix_export:").
    private static bool YamlHasExportRoot(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.StartsWith('#') || line.StartsWith('%') || line.StartsWith("---", StringComparison.Ordinal)) continue;
            return line.StartsWith(ExportRoot + ":", StringComparison.Ordinal)
                && (line.Length == ExportRoot.Length + 1 || char.IsWhiteSpace(line[ExportRoot.Length + 1]));
        }
        return false;
    }

    internal static string DetectFormat(string text)
    {
        string head = text.TrimStart();
        return head.StartsWith('{') ? "json" : head.StartsWith('<') ? "xml" : "yaml";
    }
}
