// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Links templates to hosts (<c>host.massadd</c>).</para>
/// </summary>
[Cmdlet(VerbsCommon.Add, "ZbxHostTemplate", SupportsShouldProcess = true)]
[OutputType(typeof(ZabbixHost))]
public sealed class AddZbxHostTemplateCommand : ZabbixServerCmdlet
{
    /// <summary>The hosts: ids, names or objects.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("Id", "HostId")]
    [ZabbixReference<ZabbixHost>]
    [ZabbixCompleter<ZabbixHost>]
    public ZabbixReference<ZabbixHost>[]? InputObject { get; set; }

    /// <summary>The template.</summary>
    [Parameter(Mandatory = true, Position = 1)]
    [Alias("TemplateId")]
    [ZabbixReference<ZabbixTemplate>]
    [ZabbixCompleter<ZabbixTemplate>]
    public ZabbixReference<ZabbixTemplate>? Template { get; set; }

    /// <summary>Changes the hosts without asking, even when <c>$ConfirmPreference</c> would; <c>-WhatIf</c> and an explicit <c>-Confirm</c> still apply.</summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    /// <summary>Writes the hosts, read back after the change; without it the command writes nothing.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    private readonly List<ZabbixReference<ZabbixHost>> _hosts = [];

    /// <inheritdoc/>
    protected override void ProcessRecord() => _hosts.AddRange(InputObject!);

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (_hosts.Count == 0) return;
        if (!TryRun(async cancellationToken => (Hosts: await ResolveTargetsAsync(_hosts, cancellationToken).ConfigureAwait(false), Templates: await ResolveAsync([Template!], cancellationToken).ConfigureAwait(false)),
            out (IReadOnlyList<(ulong Id, string Target)> Hosts, IReadOnlyList<ulong> Templates) resolved, _hosts.ToArray()) || resolved.Templates.Count == 0) return;
        IReadOnlyList<ulong> hostIds = Confirmed(resolved.Hosts, $"Link template {Template}", Force);
        if (hostIds.Count == 0) return;
        var request = new ZabbixHostMassAddRequest
        {
            Hosts = [.. hostIds.Select(id => new ZabbixHostMassAddHostRequest { HostId = id })],
            Templates = [.. resolved.Templates.Select(id => new ZabbixHostMassAddTemplateRequest { TemplateId = id })],
        };
        if (!TryRun(async cancellationToken =>
        {
            await ZabbixClient.Hosts.MassAddAsync(request, cancellationToken).ConfigureAwait(false);
            return PassThru ? await ZabbixEntities.Of<ZabbixHost>().SelectByIdAsync(ZabbixClient, hostIds, cancellationToken).ConfigureAwait(false) : [];
        }, out IReadOnlyList<ZabbixHost>? hosts, hostIds)) return;
        foreach (ZabbixHost host in hosts) WriteObject(host);
    }
}
