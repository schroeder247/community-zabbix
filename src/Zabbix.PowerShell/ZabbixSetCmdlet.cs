// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Provides the base of every <c>Set-Zbx&lt;Entity&gt;</c> cmdlet (the generator emits the derived classes): references
/// gathered from the pipeline, resolved in one <c>.get</c>, confirmed per object with
/// <see cref="Cmdlet.ShouldProcess(string, string)"/> alone (the generated <c>[Cmdlet]</c> attribute carries
/// <c>ConfirmImpact.Medium</c>, the same contract <see cref="ZabbixNewCmdlet{TEntity}"/> uses), updated in one batched
/// call; with <c>-PassThru</c>, read back in one <c>.get</c> for the whole batch of confirmed ids and written as the
/// entity (the shape <c>Get-Zbx&lt;Entity&gt;</c> returns), otherwise a sink. The derived
/// class declares <see cref="InputObject"/>, one <c>[Parameter]</c> per <c>*UpdateRequest</c> field (left
/// <see langword="null"/> when not bound, so the request never sends it — the JSON context ignores
/// <see langword="null"/> members), and <see cref="BuildRequest"/>.
/// </summary>
/// <typeparam name="TEntity">The object changed.</typeparam>
/// <typeparam name="TRequest">The entity's <c>*UpdateRequest</c>.</typeparam>
public abstract class ZabbixSetCmdlet<TEntity, TRequest> : ZabbixServerCmdlet
{
    // One entry per run of pipeline records whose by-property-name values are the same: those values, and the objects
    // they apply to: a piped template's own Groups bind by property name per record, and a request built once at the
    // end from whatever the parameters held last would give every template the last one's groups, which template.update
    // REPLACES.
    private readonly List<(object?[] Values, List<ZabbixReference<TEntity>> References)> _records = [];
    private System.Reflection.PropertyInfo[]? _perRecord;

    /// <summary>
    /// What to change: ids, names or objects. The derived class declares it with
    /// <c>[Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]</c>, <c>[ZabbixReference&lt;TEntity&gt;]</c>
    /// and <c>[ZabbixCompleter&lt;TEntity&gt;]</c>.
    /// </summary>
    public abstract ZabbixReference<TEntity>[]? InputObject { get; set; }

    /// <summary>
    /// Changes without a confirmation prompt even where <c>$ConfirmPreference</c> is at or below
    /// <c>ConfirmImpact.Medium</c>; <c>-WhatIf</c> and an explicit <c>-Confirm</c> still apply
    /// (<see cref="ZabbixCmdlet.ShouldProcessUnlessForced"/>).
    /// </summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    /// <summary>
    /// Writes the changed objects, read back in one <c>.get</c> for the batch; without it the command writes nothing
    /// and makes no read-back call.
    /// </summary>
    /// <remarks>
    /// A sink unless asked, like every <c>Set-</c> PowerShell has (Learn, "Strongly Encouraged Development Guidelines",
    /// "Support the PassThru Parameter").
    /// </remarks>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <summary>
    /// The piped object itself, beside <see cref="InputObject"/>'s reference to it: what the base reads to tell a piped
    /// Zabbix record, whose properties bind only what is its own (<see cref="ZabbixPipedRecord"/>), from an
    /// <c>Import-Csv</c> row, whose columns all bind.
    /// </summary>
    /// <remarks>
    /// Hidden, as <see cref="ZabbixGetCmdlet{TEntity, TParameters}.InputObject"/> is: the binder binds a piped object by
    /// value to every parameter that takes it, so this one sees the record <see cref="InputObject"/> was made from.
    /// </remarks>
    [Parameter(ValueFromPipeline = true, DontShow = true)]
    public PSObject? PipedRecord { get; set; }

    // The parameters written on the command line: what the pipeline binds is what a record adds to them.
    private HashSet<string> _written = [];

    /// <inheritdoc/>
    protected override void BeginProcessing()
    {
        _written = [.. MyInvocation.BoundParameters.Keys];
        base.BeginProcessing();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Every field parameter takes a piped record's property of its name: an <c>Import-Csv</c> row with <c>HostId</c> and
    /// <c>Description</c> columns changes each row's host. A piped Zabbix record (<c>Get-ZbxHost | Set-ZbxHost</c>) keeps
    /// only what is its own (<see cref="ZabbixPipedRecord"/>), so the host's own fields are never sent back as changes.
    /// </remarks>
    protected override void ProcessRecord()
    {
        // A piped $null is no input.
        if (MyInvocation.ExpectingInput && PipedRecord is null) return;
        if (PipedRecord?.BaseObject is { } piped)
        {
            if (ZabbixPipedRecord.IsZabbixRecord(piped)) ZabbixPipedRecord.DropForeign(this, piped.GetType(), _written, nameof(InputObject));
            // A piped object that binds nothing (a hashtable, a record without the object's id beside a written
            // -InputObject) is refused as the binder refuses one, rather than changing the written objects with nothing.
            if (!ZabbixPipedRecord.BoundAny(this, _written, nameof(PipedRecord)))
            {
                WriteError(new ErrorRecord(new ParameterBindingException("The input object cannot be bound to any parameter of the command: " + ZabbixPipedRecord.WhyUnbound(piped) + "."),
                    ZabbixPipedRecord.EngineInputNotBound, ErrorCategory.InvalidArgument, PipedRecord));
                return;
            }
        }
        // The parameters PowerShell rebinds per record: every one that takes a value by property name, InputObject aside.
        _perRecord ??= [.. GetType().GetProperties().Where(property => property.Name != nameof(InputObject) && property.Name != nameof(PipedRecord)
            && property.GetCustomAttributes(typeof(ParameterAttribute), inherit: true).OfType<ParameterAttribute>().Any(use => use.ValueFromPipelineByPropertyName))];
        object?[] values = [.. _perRecord.Select(property => property.GetValue(this))];
        if (_records.Count > 0 && Same(_records[^1].Values, values)) _records[^1].References.AddRange(InputObject ?? []);
        else _records.Add((values, [.. InputObject ?? []]));
    }

    private static bool Same(object?[] left, object?[] right)
    {
        for (int i = 0; i < left.Length; i++)
        {
            bool same = (left[i], right[i]) switch
            {
                (null, null) => true,
                (IEnumerable a, IEnumerable b) when left[i] is not string => a.Cast<object?>().SequenceEqual(b.Cast<object?>()),
                var (a, b) => Equals(a, b),
            };
            if (!same) return false;
        }
        return true;
    }

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (_records.All(record => record.References.Count == 0)) return;
        string api = ZabbixEntities.Of<TEntity>().Api;
        // One request per id; a later record's values for the same id win, as running the records one by one would.
        var requests = new Dictionary<ulong, TRequest>();
        foreach ((object?[] values, List<ZabbixReference<TEntity>> references) in _records)
        {
            if (references.Count == 0) continue;
            for (int i = 0; i < values.Length; i++) _perRecord![i].SetValue(this, values[i]);
            if (!TryRun(cancellationToken => ResolveTargetsAsync(references, cancellationToken), out IReadOnlyList<(ulong Id, string Target)>? targets, references.ToArray())) continue;
            // A reference a field names that resolves to nothing stops these objects' change: the request would
            // otherwise REPLACE the field with the part that resolved (-Template 'typo' would unlink all).
            if (!TryRun(PrepareRequestAsync, references.ToArray())) continue;
            // Confirmed per object by name (host 'web-01' (10084)), so -WhatIf lists what would change.
            foreach (ulong id in Confirmed(targets, $"Set {api}", Force))
                requests[id] = BuildRequest(id);
        }
        if (requests.Count == 0) return;
        ulong[] confirmed = [.. requests.Keys];
        if (TryRun(cancellationToken => UpdateAndReadBackAsync([.. requests.Values], PassThru, cancellationToken), out IReadOnlyList<TEntity>? updated, confirmed))
        {
            foreach (TEntity entity in updated) WriteObject(entity);
        }
    }

    private async Task<IReadOnlyList<TEntity>> UpdateAndReadBackAsync(IReadOnlyList<TRequest> requests, bool readBack, CancellationToken cancellationToken)
    {
        IReadOnlyList<ulong> ids = await UpdateAsync(ZabbixClient, requests, cancellationToken).ConfigureAwait(false);
        return readBack
            ? await ZabbixEntities.Of<TEntity>().SelectByIdAsync(ZabbixClient, ids, cancellationToken).ConfigureAwait(false)
            : [];
    }

    /// <summary>
    /// Resolves any <see cref="ZabbixReference{TEntity}"/>-array parameter (a nested collection field such
    /// as <c>-Group</c>/<c>-Template</c>, one <c>.get</c> per distinct referenced entity type) into the ids
    /// <see cref="BuildRequest"/> needs, once for the whole confirmed batch — before any request is built, so a name
    /// resolves exactly once no matter how many ids are confirmed. The base does nothing; a generated <c>Set-</c>
    /// cmdlet with a reference-array field overrides it.
    /// </summary>
    /// <param name="cancellationToken">The operation's token.</param>
    protected virtual Task PrepareRequestAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The confirmed id's request: <c>new TRequest { IdField = id.ToString(), Field = Field, ... }</c> (only bound parameters set).</summary>
    /// <param name="id">The confirmed id.</param>
    protected abstract TRequest BuildRequest(ulong id);

    /// <summary>The one call: <c>client.&lt;Entities&gt;.UpdateAsync(requests, cancellationToken)</c>.</summary>
    /// <param name="client">The server.</param>
    /// <param name="requests">One request per confirmed id.</param>
    /// <param name="cancellationToken">The stop token.</param>
    protected abstract Task<IReadOnlyList<ulong>> UpdateAsync(ZabbixClient client, IReadOnlyList<TRequest> requests, CancellationToken cancellationToken);
}
