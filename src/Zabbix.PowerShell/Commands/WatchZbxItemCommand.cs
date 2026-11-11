// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Management.Automation;
using Community.Zabbix.PowerShell.Diagnostics;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Watches items on a cadence: every value they collect from the moment the watch starts, kept running as a session.</para>
/// <para type="description">
/// The scope is <c>-HostName</c> and <c>-Key</c> (one item), <c>-HostName</c> alone (a host's items), <c>-Group</c> (a
/// host group's items), or every host when none are given. Read the values with <c>Receive-ZbxWatch -Name</c>; stop it
/// with <c>Stop-ZbxWatch -Name</c>. It starts a session, so it honours <c>-WhatIf</c> and <c>-Confirm</c>
/// (ConfirmImpact Medium), as the <c>Start-Zbx</c> session commands do.
/// </para>
/// </summary>
[Cmdlet(VerbsCommon.Watch, "ZbxItem", SupportsShouldProcess = true, DefaultParameterSetName = ParameterSets.Every)]
[OutputType(typeof(ZabbixWatch))]
public sealed class WatchZbxItemCommand : ZabbixServerCmdlet
{
    /// <summary>The name this watch is kept under; unique among the runspace's watches.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrWhiteSpace]
    public string? Name { get; set; }

    /// <summary>The host's technical name: its items, or with <c>-Key</c> its one item. Every host when neither it nor <c>-Group</c> is given.</summary>
    [Parameter(Mandatory = true, Position = 1, ParameterSetName = ParameterSets.Host)]
    [ZabbixCompleter<ZabbixHost>]
    public string? HostName { get; set; }

    /// <summary>The item key on <c>-HostName</c>: that host's one item.</summary>
    [Parameter(Position = 2, ParameterSetName = ParameterSets.Host)]
    public string? Key { get; set; }

    /// <summary>A host group's items.</summary>
    [Parameter(Mandatory = true, ParameterSetName = ParameterSets.Group)]
    public string? Group { get; set; }

    /// <summary>How often to read what the items collected; 30 seconds by default. Takes Zabbix's time suffix (<c>'30s'</c>, <c>'5m'</c>), seconds or a time span.</summary>
    [Parameter]
    [ZabbixDuration]
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Writes the watch to the pipeline.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <inheritdoc/>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The watch is disposed through the async bridge (TryRun) when TryAdd loses the name race, and owned by ZabbixContext.Sessions (disposed on Stop-ZbxWatch, Remove-Module or the runspace closing) once TryAdd succeeds; the analyzer cannot see either transfer.")]
    protected override void ProcessRecord()
    {
        IZabbixNode node = HostName is not null
            ? Key is not null ? ZabbixClient.Hosts[HostName].Items[Key] : ZabbixClient.Hosts[HostName]
            : Group is not null ? ZabbixClient.HostGroups[Group]
            : ZabbixClient.AllHosts;

        if (ZabbixContext.Sessions.TryGet(Name!, out ZabbixWatch? _))
        {
            WriteError(new ErrorRecord(new InvalidOperationException($"a watch named '{Name}' is already running"), "ZabbixWatchNameInUse", ErrorCategory.ResourceExists, Name));
            return;
        }

        string scope = HostName is not null ? Key is not null ? $"item '{Key}' on host '{HostName}'" : $"items of host '{HostName}'"
            : Group is not null ? $"items of host group '{Group}'" : "items of every host";
        if (!ShouldProcess($"{scope} as watch '{Name}'", "Start item watch")) return;

        using Activity? span = ZabbixPowerShellTelemetry.StartCommandSpan(MyInvocation.MyCommand.Name);
        IZabbixWatcher watcher = node.Watch(new ZabbixWatchPlan(Interval));
        var watch = new ZabbixWatch(Name!, watcher, Connected, Connected.Time);
        // TryAdd is the authoritative gate (the TryGet above only avoids starting and then immediately stopping a
        // watcher this call would not keep); a race lost here — two commands naming the same watch in the same
        // instant — stops the watcher this call started, through the same async bridge every command uses.
        if (!ZabbixContext.Sessions.TryAdd(watch))
        {
            TryRun(async cancellationToken =>
            {
                await watch.DisposeAsync().ConfigureAwait(false);
                return true;
            }, out _, Name);
            WriteError(new ErrorRecord(new InvalidOperationException($"a watch named '{Name}' is already running"), "ZabbixWatchNameInUse", ErrorCategory.ResourceExists, Name));
            return;
        }
        if (PassThru) WriteObject(watch);
    }

    private static class ParameterSets
    {
        public const string Every = "Every";
        public const string Host = "Host";
        public const string Group = "Group";
    }
}
