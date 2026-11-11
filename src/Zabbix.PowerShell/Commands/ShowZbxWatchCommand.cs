// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Management.Automation;
using System.Runtime.CompilerServices;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Renders a running <c>Watch-ZbxItem</c> watch's current values as a console table, once, or with <c>-Live</c> redrawn until stopped.</para>
/// </summary>
[Cmdlet(VerbsCommon.Show, "ZbxWatch")]
[OutputType(typeof(string))]
public sealed class ShowZbxWatchCommand : ZabbixCmdlet
{
    /// <summary>The watch's name.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrWhiteSpace]
    public string? Name { get; set; }

    /// <summary>Keeps refreshing until stopped (Ctrl+C), instead of rendering the current snapshot once.</summary>
    [Parameter]
    public SwitchParameter Live { get; set; }

    /// <summary>How often to redraw the table under <c>-Live</c>: Zabbix's time suffix (<c>'2s'</c>), seconds or a time span.</summary>
    [Parameter]
    [ZabbixDuration]
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (!ZabbixContext.Sessions.TryGet(Name!, out ZabbixWatch? watch))
        {
            WriteError(new ErrorRecord(new ItemNotFoundException($"no watch is named '{Name}'"), "ZabbixWatchNotFound", ErrorCategory.ObjectNotFound, Name));
            return;
        }
        WriteAll(RenderAsync(watch, ZabbixContext.Time), watch);
    }

    private async IAsyncEnumerable<string> RenderAsync(ZabbixWatch watch, TimeProvider time, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using PeriodicTimer? timer = Live ? new PeriodicTimer(Interval, time) : null;
        do
        {
            yield return Render(watch);
        }
        while (Live && timer is not null && await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private static string Render(ZabbixWatch watch)
    {
        var rows = watch.Current
            .OrderBy(entry => entry.Key.Host, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.Key.ToString(), StringComparer.Ordinal)
            .Select(entry => new string?[] { entry.Key.Host, entry.Key.Key.ToString(), entry.Value.Value, entry.Value.Time.ToString("O", CultureInfo.InvariantCulture) })
            .ToList();
        string table = ZabbixAnsiRendering.Table(["Host", "Key", "Value", "Clock"], rows);
        return $"{watch.Name}  (succeeded={watch.SucceededPolls} failed={watch.FailedPolls} dropped={watch.Dropped})\n{table}";
    }
}
