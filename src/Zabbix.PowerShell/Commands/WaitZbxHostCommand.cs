// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Waits for a host to exist — the autoregistration companion.</para>
/// <para type="description">An agent's first autoregistration attempt always fails by design; the host appears on its next refresh. Polls <c>host.get</c> every <c>-Interval</c> until the host appears or <c>-Timeout</c> passes (each a Zabbix time suffix such as <c>'5s'</c>, seconds or a time span).</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Wait, "ZbxHost")]
[OutputType(typeof(ZabbixHost))]
public sealed class WaitZbxHostCommand : ZabbixServerCmdlet
{
    /// <summary>The host's technical name (exact match).</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [ValidateNotNullOrWhiteSpace]
    [ZabbixCompleter<ZabbixHost>]
    public string? Name { get; set; }

    /// <summary>How long to wait: Zabbix's time suffix (<c>'2m'</c>), seconds or a time span; two minutes by default (two active-agent refresh cycles at the common 60s interval).</summary>
    [Parameter]
    [ZabbixDuration]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How often to poll: Zabbix's time suffix (<c>'5s'</c>), seconds or a time span.</summary>
    [Parameter]
    [ZabbixDuration]
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (TryRun(async cancellationToken =>
            {
                var parameters = new ZabbixHostGetParameters { Filter = new() { ["host"] = Name! } };
                TimeProvider time = Connected.Time;
                // A deadline on the monotonic clock: setting the machine's clock neither ends nor extends the wait.
                long waitStarted = time.GetTimestamp();
                TimeSpan timeout = Timeout;
                using var timer = new PeriodicTimer(Interval, time);
                while (true)
                {
                    IReadOnlyList<ZabbixHost> hosts = await ZabbixClient.Hosts.GetAsync(parameters, cancellationToken).ConfigureAwait(false);
                    if (hosts.Count > 0) return hosts[0];
                    if (time.GetElapsedTime(waitStarted) >= timeout || !await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                        throw new TimeoutException($"host '{Name}' did not appear within {Timeout}");
                }
            }, out ZabbixHost? host, Name))
        {
            WriteObject(host);
        }
    }
}
