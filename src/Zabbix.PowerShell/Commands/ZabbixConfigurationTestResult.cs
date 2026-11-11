// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell.Commands;

/// <summary>The verdict <c>Test-ZbxConfiguration</c> returns for one document.</summary>
public sealed class ZabbixConfigurationTestResult
{
    /// <summary>True when the document parsed cleanly.</summary>
    public bool IsValid { get; init; }

    /// <summary>Where the document came from (a file path or the pipeline).</summary>
    public string? Source { get; init; }

    /// <summary>The format it was validated as.</summary>
    public string? Format { get; init; }

    /// <summary>How many plaintext secrets it carries.</summary>
    public int PlaintextSecretCount { get; init; }

    /// <summary>Everything that would trip the importer, empty when valid.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];
}
