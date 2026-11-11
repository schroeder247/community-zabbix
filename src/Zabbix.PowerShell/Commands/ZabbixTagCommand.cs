// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// Provides what <c>Add-ZbxTag</c> and <c>Remove-ZbxTag</c> share: the hosts, always <c>-InputObject</c>, and the tags, in
/// two parameter sets. <b>ByName</b>: the tag as <c>-Name</c>/<c>-Value</c> by position or by property name from piped
/// records, the hosts from the pipeline or named (<c>Get-ZbxHost web01 | Add-ZbxTag env prod</c>, <c>Add-ZbxTag env prod
/// -InputObject web01</c>). <b>ByTag</b>: the tags from the pipeline (<c>New-ZbxTag env prod | Add-ZbxTag -InputObject
/// web01</c>, a read host's <c>.Tags</c>, a <c>[pscustomobject]</c> with <c>Name</c>/<c>Value</c>), the hosts named. Every tag
/// applies to every host, in one <c>host.update</c> per host.
/// </summary>
public abstract class ZabbixTagCommand : ZabbixServerCmdlet
{
    /// <summary>The parameter set that takes the hosts from the pipeline and the tag by name.</summary>
    public const string ByName = "ByName";

    /// <summary>The parameter set that takes the tags from the pipeline and the hosts as <c>-InputObject</c>.</summary>
    public const string ByTag = "ByTag";

    /// <summary>
    /// The hosts: ids, names or objects. From the pipeline when the tag is given by name; named when the tags come from
    /// the pipeline.
    /// </summary>
    [Parameter(Mandatory = true, ValueFromPipeline = true, ParameterSetName = ByName)]
    [Parameter(Mandatory = true, ParameterSetName = ByTag)]
    [Alias("Id")]
    [ZabbixReference<ZabbixHost>]
    [ZabbixCompleter<ZabbixHost>]
    public ZabbixReference<ZabbixHost>[]? InputObject { get; set; }

    /// <summary>The tag's name; binds by property name from a piped record carrying <c>Name</c>.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true, ParameterSetName = ByName)]
    [ValidateNotNullOrWhiteSpace]
    public string? Name { get; set; }

    /// <summary>The tag's value (on <c>Remove-ZbxTag</c>, every value when omitted); binds by property name from a piped record.</summary>
    [Parameter(Position = 1, ValueFromPipelineByPropertyName = true, ParameterSetName = ByName)]
    public string? Value { get; set; }

    /// <summary>The tags, from the pipeline: <c>New-ZbxTag</c>'s output, a read host's <c>.Tags</c>, or any object or hashtable with a tag (or name) and a value.</summary>
    [Parameter(Mandatory = true, ValueFromPipeline = true, ParameterSetName = ByTag)]
    [ZabbixTagPair]
    public ZabbixTagRequest[]? Tag { get; set; }

    /// <summary>Writes the changed hosts back (one <c>host.get</c> for the whole batch), instead of nothing.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <summary>Every host of the call, in order, once.</summary>
    private protected List<ZabbixReference<ZabbixHost>> Hosts { get; } = [];

    /// <summary>Every tag of the call, in order, once.</summary>
    private protected List<(string Name, string? Value)> TagPairs { get; } = [];

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        foreach (ZabbixReference<ZabbixHost> host in InputObject ?? [])
            if (!Hosts.Contains(host)) Hosts.Add(host);
        IEnumerable<(string Name, string? Value)> given = ParameterSetName == ByTag
            ? (Tag ?? []).Select(tag => (tag.Tag ?? string.Empty, (string?)tag.Value))
            : [(Name!, Value)];
        foreach ((string name, string? value) in given)
            if (name.Length > 0 && !TagPairs.Contains((name, value))) TagPairs.Add((name, value));
    }
}

internal static class ZabbixTagCommandText
{
    internal static string TagList(IEnumerable<(string Name, string? Value)> tags)
        => string.Join(", ", tags.Select(tag => tag.Value is null ? tag.Name : tag.Name + "=" + tag.Value));
}
