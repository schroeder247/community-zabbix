// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Provides the base of every <c>Remove-Zbx&lt;Entity&gt;</c> cmdlet: references gathered from the pipeline, resolved in
/// one <c>.get</c>, confirmed per object with <see cref="Cmdlet.ShouldProcess(string, string)"/> alone (the generated
/// <c>[Cmdlet]</c> attribute carries <c>ConfirmImpact.High</c>, so this prompts by default; no second
/// <see cref="Cmdlet.ShouldContinue(string, string, ref bool, ref bool)"/> is layered on an ordinary delete), deleted in
/// one call under the stop token — or, when the server refuses that batch for one of its objects, one call per object,
/// so the rest go and the bad one is its own error record. The derived class declares <see cref="InputObject"/> (its
/// attributes are keyed on the entity) and the one call.
/// </summary>
/// <typeparam name="TEntity">The object removed.</typeparam>
public abstract class ZabbixRemoveCmdlet<TEntity> : ZabbixServerCmdlet
{
    private readonly List<ZabbixReference<TEntity>> _references = [];

    /// <summary>
    /// What to remove: ids, names or objects. The derived class declares it with
    /// <c>[Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]</c>, <c>[ZabbixReference&lt;TEntity&gt;]</c>
    /// and <c>[ZabbixCompleter&lt;TEntity&gt;]</c>.
    /// </summary>
    public abstract ZabbixReference<TEntity>[]? InputObject { get; set; }

    /// <summary>
    /// Removes without the confirmation prompt (ConfirmImpact High), so a script or a host that cannot prompt can delete;
    /// <c>-WhatIf</c> and an explicit <c>-Confirm</c> still apply.
    /// </summary>
    /// <remarks>
    /// Skips the <see cref="Cmdlet.ShouldProcess(string, string)"/> prompt, as Learn's RD05 guideline describes for
    /// <c>Force</c> (<see cref="ZabbixCmdlet.ShouldProcessUnlessForced"/>).
    /// </remarks>
    [Parameter]
    public SwitchParameter Force { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord() => _references.AddRange(InputObject ?? []);

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (_references.Count == 0) return;
        // Resolved first, then confirmed per object by name (host 'web-01' (10084)), so -WhatIf lists what would go.
        if (!TryRun(cancellationToken => ResolveTargetsAsync(_references, cancellationToken), out IReadOnlyList<(ulong Id, string Target)>? targets, _references.ToArray())) return;
        string api = ZabbixEntities.Of<TEntity>().Api;
        ulong[] confirmed = [.. Confirmed(targets, $"Remove {api}", Force)];
        if (confirmed.Length == 0) return;
        if (confirmed.Length == 1)
        {
            TryRun(cancellationToken => DeleteAsync(ZabbixClient, confirmed, cancellationToken), confirmed[0]);
            return;
        }
        if (!TryRun(cancellationToken => DeleteBatchAsync(confirmed, cancellationToken), out bool deleted, confirmed) || deleted) return;
        // A *.delete is one transaction: one id that does not exist (or cannot be deleted) refuses the whole batch
        // (measured on 7.0.31, 7.4.15 and 8.0.0: "No permissions to referred object or it does not exist!", nothing deleted). One
        // call per id then removes the rest and makes the bad one its own error record.
        WriteVerbose($"{api}.delete refused the batch of {confirmed.Length}; removing them one at a time");
        foreach (ulong id in confirmed)
            TryRun(cancellationToken => DeleteAsync(ZabbixClient, [id], cancellationToken), id);
    }

    /// <summary>
    /// Deletes <paramref name="ids"/> in one call; <see langword="false"/> when the server refused the batch for what
    /// is in it (an id it does not know, a validation or database refusal of one object), which a call per id tells
    /// apart. A refusal of the caller (the token, the role, a missing method) or of the transport fails every call the
    /// same way, so it is the command's one error.
    /// </summary>
    private async Task<bool> DeleteBatchAsync(ulong[] ids, CancellationToken cancellationToken)
    {
        try
        {
            await DeleteAsync(ZabbixClient, ids, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ZabbixApiException refused) when (refused.Kind is ZabbixApiErrorKind.PermissionOrNotFound or ZabbixApiErrorKind.Validation or ZabbixApiErrorKind.Database)
        {
            return false;
        }
    }

    /// <summary>The one call: <c>client.&lt;Entities&gt;.DeleteAsync(ids, cancellationToken)</c>.</summary>
    /// <param name="client">The server.</param>
    /// <param name="ids">The confirmed ids.</param>
    /// <param name="cancellationToken">The stop token.</param>
    protected abstract Task DeleteAsync(ZabbixClient client, IReadOnlyList<ulong> ids, CancellationToken cancellationToken);
}
