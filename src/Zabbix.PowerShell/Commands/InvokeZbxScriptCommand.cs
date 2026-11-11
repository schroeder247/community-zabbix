// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Runs a script against a host or event (<c>script.execute</c>).</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "ZbxScript", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium, DefaultParameterSetName = ParameterSets.Host)]
[OutputType("Community.Zabbix.PowerShell.ScriptResult")]
public sealed class InvokeZbxScriptCommand : ZabbixServerCmdlet
{
    /// <summary>The script.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    [Alias("Id")]
    [ZabbixReference<ZabbixScript>]
    [ZabbixCompleter<ZabbixScript>]
    public ZabbixReference<ZabbixScript>? Script { get; set; }

    /// <summary>The host to run it against: an id, a name, or a piped host (by its <c>HostId</c>).</summary>
    [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true, ParameterSetName = ParameterSets.Host)]
    [Alias("Host", "HostId")]
    [ZabbixReference<ZabbixHost>]
    [ZabbixCompleter<ZabbixHost>]
    public ZabbixReference<ZabbixHost>? HostReference { get; set; }

    /// <summary>The event to run it against, instead of a host.</summary>
    [Parameter(Mandatory = true, ParameterSetName = ParameterSets.Event)]
    public ulong? EventId { get; set; }

    /// <summary>The manual input the script's <c>{MANUALINPUT}</c> macro reads, when the script asks for one.</summary>
    [Parameter]
    public string? ManualInput { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        // Resolved first, so the prompt names the script and the host it runs on ("Run script 'ping' (1)" on
        // "host 'web-01' (10084)"); a name that matches nothing is its own error record and nothing runs.
        if (!TryRun(ResolveAsync, out ((ulong Id, string Target) Script, (ulong Id, string Target)? Host)? resolved, Script) || resolved is not { } run) return;
        string target = run.Host?.Target ?? $"event {EventId}";
        if (!ShouldProcess(target, $"Run {run.Script.Target}")) return;
        var request = new ZabbixScriptExecuteRequest { ScriptId = run.Script.Id, HostId = run.Host?.Id, EventId = EventId, ManualInput = ManualInput };
        if (TryRun(cancellationToken => ZabbixClient.Scripts.ExecuteAsync(request, cancellationToken), out JsonElement executed, target))
            WriteObject(ZabbixJsonShape.ToShell(executed, "Community.Zabbix.PowerShell.ScriptResult"));
    }

    private async Task<((ulong Id, string Target) Script, (ulong Id, string Target)? Host)?> ResolveAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(ulong Id, string Target)> scripts = await ResolveTargetsAsync([Script!], cancellationToken).ConfigureAwait(false);
        if (scripts.Count == 0) return null;
        if (HostReference is not { } host) return (scripts[0], null);
        IReadOnlyList<(ulong Id, string Target)> hosts = await ResolveTargetsAsync([host], cancellationToken).ConfigureAwait(false);
        return hosts.Count == 0 ? null : (scripts[0], hosts[0]);
    }

    private static class ParameterSets
    {
        public const string Host = "Host";
        public const string Event = "Event";
    }
}
