// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Deletes web scenarios (<c>httptest.delete</c>).</para>
/// <para type="description">
/// <c>-Id</c> takes the scenario ids, not names; piped ids and piped objects with an <c>HttpTestId</c> (e.g. from
/// <c>Get-ZbxHttpTest</c>) bind to it, and every id collected from the pipeline is deleted in one call. Asks for
/// confirmation for each scenario (ConfirmImpact High); pass <c>-Confirm:$false</c> to skip it. Writes nothing.
/// </para>
/// </summary>
/// <remarks>
/// Hand-written, not generated (see <see cref="NewZbxHttpTestCommand"/>). <c>-Id</c> takes the scenario's id.
/// </remarks>
[Cmdlet(VerbsCommon.Remove, "ZbxHttpTest", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
public sealed class RemoveZbxHttpTestCommand : ZabbixServerCmdlet
{
    private readonly List<ulong> _ids = [];

    /// <summary>The scenario id(s).</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [Alias("HttpTestId")]
    [ZabbixId]
    public ulong[] Id { get; set; } = [];

    /// <summary>Removes without the confirmation prompt; <c>-WhatIf</c> and an explicit <c>-Confirm</c> still apply.</summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord() => _ids.AddRange(Id);

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        ulong[] confirmed = [.. _ids.Distinct().Where(id => ShouldProcessUnlessForced($"httptest {id}", "Remove httptest", Force))];
        if (confirmed.Length == 0) return;
        TryRun(cancellationToken => ZabbixClient.HttpTests.DeleteAsync(confirmed, cancellationToken), confirmed);
    }
}
