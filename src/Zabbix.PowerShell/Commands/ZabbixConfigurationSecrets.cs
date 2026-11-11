// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.RegularExpressions;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// Provides the secret-disclosure gate for <c>configuration.export</c>. The export carries media-type passwords
/// (<c>password</c>), IPMI passwords (<c>ipmi_password</c>) and OAuth client secrets (<c>client_secret</c>) in the
/// clear (pinned against <c>CConfigurationExportBuilder.php</c>: 381, 492, 557, 672, 1205 across 7.0–8.0) — SNMPv3
/// auth/priv passphrases and HTTP/script auth also ride the item/discovery-rule <c>password</c> field, so the same
/// field name covers them. A field-name scan (not a full document model — the SDK has none yet for configuration
/// documents) over whichever wire shape the export used (JSON/XML/YAML all name the field the same way) finds every
/// occurrence; a value already empty is not a finding (an unset secret discloses nothing).
/// </summary>
internal static class ZabbixConfigurationSecrets
{
    private const string Redacted = "***REDACTED***";

    private static readonly Regex s_jsonSecret = new(
        "\"(password|ipmi_password|client_secret)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_xmlSecret = new(
        "<(password|ipmi_password|client_secret)>([^<]+)</\\1>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_yamlSecret = new(
        "^([ \\t]*(?:-[ \\t]+)?)(password|ipmi_password|client_secret):[ \\t]*(\\S.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>Redacts every plaintext secret field in <paramref name="text"/> and counts how many it found.</summary>
    /// <param name="text">The export/diff text.</param>
    /// <param name="format">Its wire format: <c>json</c> (also the default for anything else, including a JSON diff), <c>xml</c> or <c>yaml</c>.</param>
    public static (string Text, int Count) Redact(string text, string format)
    {
        ArgumentNullException.ThrowIfNull(text);
        int count = 0;
        string result = format switch
        {
            _ when string.Equals(format, "xml", StringComparison.OrdinalIgnoreCase) => s_xmlSecret.Replace(text, match =>
            {
                if (match.Groups[2].Value.Length == 0) return match.Value;
                count++;
                return $"<{match.Groups[1].Value}>{Redacted}</{match.Groups[1].Value}>";
            }),
            _ when string.Equals(format, "yaml", StringComparison.OrdinalIgnoreCase) => s_yamlSecret.Replace(text, match =>
            {
                string value = match.Groups[3].Value.Trim();
                if (value.Length == 0 || value is "''" or "\"\"") return match.Value;
                count++;
                return $"{match.Groups[1].Value}{match.Groups[2].Value}: {Redacted}";
            }),
            _ => s_jsonSecret.Replace(text, match =>
            {
                if (match.Groups[2].Value.Length == 0) return match.Value;
                count++;
                return $"\"{match.Groups[1].Value}\":\"{Redacted}\"";
            }),
        };
        return (result, count);
    }

    /// <summary>How many plaintext secrets <paramref name="text"/> carries, without building the redacted copy.</summary>
    public static int Count(string text, string format) => Redact(text, format).Count;
}
