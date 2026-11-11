// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets recent Zabbix failures from the engine's own <c>$Error</c>, newest first.</para>
/// <para type="description">
/// Every failure this module writes is an <c>ErrorRecord</c> whose <c>FullyQualifiedErrorId</c> starts with
/// <c>Zabbix</c> and names its kind (<c>ZabbixApi.PermissionOrNotFound</c>, <c>ZabbixWire.ConnectionRefused</c>). This
/// command reads the session's own <c>$Error</c> and writes the records a command of the Community.Zabbix modules wrote (the
/// engine's own binding errors for those commands included) or that carry an exception from the Zabbix libraries,
/// newest first: the last 16 by default, <c>-Last</c> N, or
/// all of them with <c>-AllErrors</c>. It keeps no log of its own, so how many are kept is <c>$MaximumErrorCount</c>,
/// and <c>$Error.Clear()</c> clears them.
/// </para>
/// </summary>
/// <remarks>
/// Every failure this module writes is one <see cref="ErrorRecord"/> whose id names its kind
/// (<see cref="ZabbixErrors"/>), already in the shell's own <c>$Error</c>, which <c>$MaximumErrorCount</c> bounds;
/// this command is a filtered read of it, not a second, process-wide error log.
/// </remarks>
[Cmdlet(VerbsCommon.Get, "ZbxError", DefaultParameterSetName = Recent)]
[OutputType(typeof(ErrorRecord))]
public sealed class GetZbxErrorCommand : ZabbixCmdlet
{
    private const string Recent = "Recent";
    private const string All = "All";

    /// <summary>The most recent N failures; 16 by default.</summary>
    [Parameter(Position = 0, ParameterSetName = Recent)]
    [ValidateRange(1, int.MaxValue)]
    public int Last { get; set; } = 16;

    /// <summary>Every buffered failure (bounded by <c>$MaximumErrorCount</c>), not just the most recent.</summary>
    [Parameter(Mandatory = true, ParameterSetName = All)]
    public SwitchParameter AllErrors { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        IEnumerable<ErrorRecord> zabbix = (GetVariableValue("global:Error") as IEnumerable)?
                .Cast<object>()
                .Select(item => item is PSObject wrapped ? wrapped.BaseObject : item)
                .OfType<ErrorRecord>()
                .Where(IsZabbix)
            ?? [];
        foreach (ErrorRecord error in AllErrors ? zabbix : zabbix.Take(Last)) WriteObject(error);
    }

    // By who wrote it, not by its text: a command of the three modules, or an exception from the Zabbix libraries.
    private static bool IsZabbix(ErrorRecord record)
        => record.InvocationInfo?.MyCommand?.ModuleName is "Community.Zabbix" or "Community.Zabbix.Agent" or "Community.Zabbix.Proxy"
           || record.FullyQualifiedErrorId.StartsWith("Zabbix", StringComparison.Ordinal)
           || record.Exception?.GetType().Namespace is { } ns && ns.StartsWith("Community.Zabbix", StringComparison.Ordinal);
}
