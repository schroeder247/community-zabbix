// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets server-side tasks (<c>task.get</c>): the status of a check-now, diagnostic or reload previously requested.</para>
/// <para type="description">
/// By id (piped objects with a <c>TaskId</c>, e.g. from <c>New-ZbxTask</c>, bind to <c>-Id</c>), or every task the
/// server still tracks. There is no name, <c>-Filter</c> or search selection.
/// </para>
/// </summary>
/// <remarks>
/// Hand-written: <c>ZabbixTaskGetParameters</c> has neither <c>filter</c> nor <c>search</c> (only <c>taskids</c>), so
/// it cannot implement <see cref="IZabbixGetParameters"/>.
/// </remarks>
[Cmdlet(VerbsCommon.Get, "ZbxTask")]
[OutputType(typeof(ZabbixTaskRecord))]
public sealed class GetZbxTaskCommand : ZabbixServerCmdlet
{
    /// <summary>The task ids; every task the server still tracks when omitted.</summary>
    [Parameter(Position = 0, ValueFromPipelineByPropertyName = true)]
    [Alias("TaskId")]
    [ZabbixId]
    public ulong[]? Id { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        var parameters = new ZabbixTaskGetParameters { TaskIds = Id is { Length: > 0 } ? [.. Id] : null };
        WriteAll(cancellationToken => ZabbixClient.Tasks.GetAsync(parameters, cancellationToken), parameters);
    }
}
