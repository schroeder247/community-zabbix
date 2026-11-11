// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Provides the base of every <c>Set-Zbx&lt;Singleton&gt;</c> cmdlet: no <c>-Id</c> (there is exactly one object to
/// change), no pipeline of references to resolve first — every field is an ordinary optional <c>[Parameter]</c> on
/// the derived class, left <see langword="null"/> when not bound; <see cref="BuildRequest"/> passes them straight through to
/// <typeparamref name="TRequest"/> (whose members are all nullable), so an unbound parameter is never sent — the JSON
/// context ignores a <see langword="null"/> member, exactly as <see cref="ZabbixSetCmdlet{TEntity,TRequest}"/> already
/// relies on. One confirmation with <see cref="Cmdlet.ShouldProcess(string, string)"/> alone (the derived class's
/// <c>[Cmdlet]</c> attribute carries <c>ConfirmImpact.Medium</c>, the same contract every generated
/// <c>New-</c>/<c>Set-Zbx&lt;Entity&gt;</c> uses), one update call, then, with <c>-PassThru</c>, one
/// <see cref="GetAsync"/> read-back so it writes the settings object the way <see cref="ZabbixSingletonGetCmdlet{TEntity}"/>
/// does; without it a sink.
/// </summary>
/// <typeparam name="TRequest">What <c>.update</c> takes.</typeparam>
public abstract class ZabbixSingletonSetCmdlet<TRequest> : ZabbixServerCmdlet
{
    /// <summary>
    /// Changes without a confirmation prompt; <c>-WhatIf</c> and an explicit <c>-Confirm</c> still apply
    /// (<see cref="ZabbixCmdlet.ShouldProcessUnlessForced"/>).
    /// </summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    /// <summary>Writes the settings as they are after the change, read back in one <c>.get</c>; without it the command writes nothing.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (!ShouldProcessUnlessForced(Subject, "Set settings", Force)) return;
        if (TryRun(UpdateAndReadBackAsync, out object? result, Subject) && PassThru)
        {
            WriteObject(result switch { System.Text.Json.JsonElement json => ZabbixJsonShape.ToShell(json), _ => result });
        }
    }

    private async Task<object?> UpdateAndReadBackAsync(CancellationToken cancellationToken)
    {
        await UpdateAsync(ZabbixClient, BuildRequest(), cancellationToken).ConfigureAwait(false);
        return PassThru ? await GetAsync(cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>What the confirmation prompt names, e.g. <c>"the housekeeping settings"</c>.</summary>
    protected abstract string Subject { get; }

    /// <summary>The request built from whichever parameters were bound.</summary>
    protected abstract TRequest BuildRequest();

    /// <summary>The one call: <c>client.&lt;Singleton&gt;.UpdateAsync(request, cancellationToken)</c>.</summary>
    /// <param name="client">The server.</param>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The stop token.</param>
    protected abstract Task<object?> UpdateAsync(ZabbixClient client, TRequest request, CancellationToken cancellationToken);

    /// <summary>The read-back after <see cref="UpdateAsync"/>: <c>client.&lt;Singleton&gt;.GetAsync(null, cancellationToken)</c>, the same call the paired <c>Get-Zbx&lt;Singleton&gt;</c> makes.</summary>
    /// <param name="cancellationToken">The stop token.</param>
    protected abstract Task<object?> GetAsync(CancellationToken cancellationToken);
}
