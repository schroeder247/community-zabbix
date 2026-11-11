// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets web scenarios (<c>httptest.get</c>).</para>
/// <para type="description">
/// All scenarios (optionally only those on <c>-HostReference</c>), the scenarios with the given ids, or by <c>-Name</c>.
/// There is no <c>-Filter</c> selection.
/// </para>
/// </summary>
/// <remarks>
/// Hand-written: <c>ZabbixHttpTestGetParameters</c> has <c>filter</c>/<c>search</c> but no
/// <c>searchWildcardsEnabled</c>, so it cannot implement <see cref="IZabbixGetParameters"/>.
/// </remarks>
[Cmdlet(VerbsCommon.Get, "ZbxHttpTest", DefaultParameterSetName = ZabbixParameterSets.All)]
[OutputType(typeof(ZabbixHttpTest))]
public sealed class GetZbxHttpTestCommand : ZabbixServerCmdlet
{
    /// <summary>The web scenario ids.</summary>
    [Parameter(Mandatory = true, Position = 0, ParameterSetName = ZabbixParameterSets.ById, ValueFromPipelineByPropertyName = true)]
    [ZabbixId]
    public ulong[]? Id { get; set; }

    /// <summary>The web scenario's name, case-insensitive; <c>*</c> and <c>?</c> are wildcards, brackets are literal.</summary>
    [Parameter(Position = 0, ParameterSetName = ZabbixParameterSets.ByName)]
    [SupportsWildcards]
    [ZabbixCompleter<ZabbixHttpTest>]
    public string? Name { get; set; }

    /// <summary>The hosts to get web scenarios for.</summary>
    [Parameter(ParameterSetName = ZabbixParameterSets.All, ValueFromPipelineByPropertyName = true)]
    [Alias("Host", "HostId")]
    [ZabbixReference<ZabbixHost>]
    [ZabbixCompleter<ZabbixHost>]
    public ZabbixReference<ZabbixHost>[]? HostReference { get; set; }

    /// <summary>At most this many.</summary>
    [Parameter(ParameterSetName = ZabbixParameterSets.All)]
    [Parameter(ParameterSetName = ZabbixParameterSets.ByName)]
    [ValidateRange(1, int.MaxValue)]
    public int? Limit { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        TryRun(async cancellationToken =>
        {
            IReadOnlyList<ulong>? hostIds = null;
            if (HostReference is { Length: > 0 })
            {
                IReadOnlyList<ulong> ids = await ResolveAsync(HostReference, cancellationToken).ConfigureAwait(false);
                hostIds = ids;
            }
            // httptest.get has search but no searchWildcardsEnabled: the server narrows by the pattern's longest literal
            // run (a case-insensitive substring every match contains), this side matches the pattern.
            ZabbixNamePattern? pattern = ParameterSetName == ZabbixParameterSets.ByName && Name is not null ? ZabbixNamePattern.Parse(Name) : null;
            var parameters = new ZabbixHttpTestGetParameters
            {
                HttpTestIds = ParameterSetName == ZabbixParameterSets.ById ? [.. Id!] : null,
                HostIds = hostIds,
                Search = pattern is { LongestLiteral.Length: > 0 }
                    ? new() { ["name"] = pattern.LongestLiteral }
                    : null,
                Limit = pattern is null ? Limit : null,
            };
            IReadOnlyList<ZabbixHttpTest> found = await ZabbixClient.HttpTests.GetAsync(parameters, cancellationToken).ConfigureAwait(false);
            if (pattern is null) return found;
            IEnumerable<ZabbixHttpTest> matched = found.Where(test => pattern.IsMatch(test.Name));
            return [.. Limit is { } limit ? matched.Take(limit) : matched];
        }, out IReadOnlyList<ZabbixHttpTest>? tests, Connected.Name);
        if (tests is not null) WriteObject(tests, enumerateCollection: true);
    }
}
