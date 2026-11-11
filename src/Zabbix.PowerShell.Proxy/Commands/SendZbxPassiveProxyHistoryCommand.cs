// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Writes a value into a running passive-proxy session's store, for the server to pull as <c>proxy data</c>.</para>
/// </summary>
[Cmdlet(VerbsCommunications.Send, "ZbxPassiveProxyHistory", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Low)]
public sealed class SendZbxPassiveProxyHistoryCommand : ZabbixCmdlet
{
    /// <summary>The session's name.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrWhiteSpace]
    public string Name { get; set; } = null!;

    /// <summary>The host the value belongs to, as configured in Zabbix.</summary>
    [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true)]
    [Alias("Host")]
    [ValidateNotNullOrWhiteSpace]
    public string? HostName { get; set; }

    /// <summary>The item key.</summary>
    [Parameter(Mandatory = true, Position = 2, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNullOrWhiteSpace]
    public string? Key { get; set; }

    /// <summary>The value.</summary>
    [Parameter(Mandatory = true, Position = 3, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNull]
    public string? Value { get; set; }

    /// <summary>When (a date, Unix seconds or <c>clock.ns</c>); now when omitted.</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    [ZabbixClock]
    public DateTimeOffset? Clock { get; set; }

    /// <summary>Nanoseconds within the second; the clock's own when omitted.</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    public int? Ns { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (!ZabbixContext.Sessions.TryGet(Name, out ZabbixPassiveProxySession? session))
        {
            WriteError(new ErrorRecord(new ItemNotFoundException($"no passive-proxy session is named '{Name}'"), "ZabbixPassiveProxyNotFound", ErrorCategory.ObjectNotFound, Name));
            return;
        }
        if (session.Store is not { } store)
        {
            WriteError(new ErrorRecord(new InvalidOperationException($"the passive-proxy session '{Name}' has no store (not started, or already stopped)"), "ZabbixPassiveProxyNotStarted", ErrorCategory.InvalidOperation, Name));
            return;
        }
        if (!ShouldProcess($"{HostName}/{Key}", "Send passive-proxy history")) return;
        TimeProvider time = ZabbixContext.Time;
        DateTimeOffset clock = Clock ?? DateTimeOffset.FromUnixTimeSeconds(time.GetUtcNow().ToUnixTimeSeconds());
        var value = new ZabbixValue(HostName!, Key!, Value!, Ns is { } ns ? ZabbixClockAttribute.WithNanoseconds(clock, ns) : clock);
        TryRun(cancellationToken => store.WriteAsync(value, cancellationToken).AsTask(), Name);
    }
}
