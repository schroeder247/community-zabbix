// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Provides the base of every <c>New-Zbx&lt;Entity&gt;</c> cmdlet (the generator emits the derived classes). The derived
/// class declares one <c>[Parameter]</c> per <c>*CreateRequest</c> field (never <c>Mandatory</c>, even for an
/// API-required field: <c>-AdditionalProperty</c> may supply it, and the request is validated after the merge) and builds the
/// request in <see cref="CreateAsync"/>. The base confirms with <see cref="Cmdlet.ShouldProcess(string, string)"/>
/// alone (<c>ConfirmImpact.Medium</c>, below the default <c>$ConfirmPreference</c> of <c>High</c>, so an ordinary call
/// prompts for nothing; <c>-WhatIf</c> previews, <c>-Confirm</c> asks), creates, then reads the created objects back in
/// one <c>.get</c> for the whole batch and writes the entity, as <c>Get-Zbx&lt;Entity&gt;</c> does — always the entity,
/// with no <c>-PassThru</c> switch. A derived class may add to what it writes (<see cref="CompleteAsync"/>). Every field
/// parameter also takes a piped object's property of its name, once per piped object (<see cref="InputObject"/>).
/// </summary>
/// <typeparam name="TEntity">The object created (for <see cref="ZabbixEntities"/>: its API name in the confirmation prompt, and the shape read back).</typeparam>
public abstract class ZabbixNewCmdlet<TEntity> : ZabbixServerCmdlet
{
    /// <summary>
    /// Creates without a confirmation prompt even where <c>$ConfirmPreference</c> is at or below
    /// <c>ConfirmImpact.Medium</c>; <c>-WhatIf</c> and an explicit <c>-Confirm</c> still apply
    /// (<see cref="ZabbixCmdlet.ShouldProcessUnlessForced"/>).
    /// </summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    /// <summary>
    /// A piped object, whose properties bind the command's parameters by name: an <c>Import-Csv</c> row or a
    /// <c>[pscustomobject]</c> with <c>Name</c>, <c>InterfaceIp</c>, … columns creates one object per row. A piped Zabbix
    /// record binds only what is its own (<see cref="ZabbixPipedRecord"/>: <c>Get-ZbxHost | New-ZbxItem</c> takes the
    /// host's id, never its name or description), and an object that binds nothing is refused.
    /// </summary>
    /// <remarks>
    /// Its own hidden parameter, as on <see cref="ZabbixGetCmdlet{TEntity, TParameters}.InputObject"/>: the binder binds a
    /// piped object to it by value and to the other parameters by property name, so the command sees which object the
    /// properties came from.
    /// </remarks>
    [Parameter(ValueFromPipeline = true, DontShow = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>
    /// The name of the object to create, for the confirmation prompt and <c>-WhatIf</c> (<c>host 'web-01'</c>);
    /// <see langword="null"/> when it has none. The generated class returns its name parameter, or the name
    /// <c>-AdditionalProperty</c> gives.
    /// </summary>
    protected virtual string? CreatedName => null;

    // The parameters written on the command line: what the pipeline binds is what a record adds to them.
    private HashSet<string> _written = [];

    /// <inheritdoc/>
    protected override void BeginProcessing()
    {
        _written = [.. MyInvocation.BoundParameters.Keys];
        base.BeginProcessing();
    }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        // A piped $null is no input: nothing to create from it (the binder gives the by-value parameter $null).
        if (MyInvocation.ExpectingInput && InputObject is null) return;
        if (InputObject is { BaseObject: { } piped })
        {
            if (ZabbixPipedRecord.IsZabbixRecord(piped)) ZabbixPipedRecord.DropForeign(this, piped.GetType(), _written, nameof(InputObject));
            if (!ZabbixPipedRecord.BoundAny(this, _written, nameof(InputObject)))
            {
                // The binder's own id, as Get- writes it: the object is not this command's input.
                WriteError(new ErrorRecord(new ParameterBindingException("The input object cannot be bound to any parameter of the command: " + ZabbixPipedRecord.WhyUnbound(piped) + "."),
                    ZabbixPipedRecord.EngineInputNotBound, ErrorCategory.InvalidArgument, InputObject));
                return;
            }
        }
        string api = ZabbixEntities.Of<TEntity>().Api;
        string target = CreatedName is { Length: > 0 } name ? $"{api} '{name}'" : $"new {api}";
        if (ShouldProcessUnlessForced(target, $"New {api}", Force) && TryRun(CreateAndReadBackAsync, out IReadOnlyList<object>? created, target))
        {
            foreach (object entity in created) WriteObject(entity);
        }
    }

    private async Task<IReadOnlyList<object>> CreateAndReadBackAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ulong> ids = await CreateAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TEntity> created = await ZabbixEntities.Of<TEntity>().SelectByIdAsync(ZabbixClient, ids, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(ids, created, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What the command writes for the objects it created: the records read back, unless a derived class adds to them
    /// (<c>New-ZbxToken -Generate</c> adds each token's secret).
    /// </summary>
    /// <param name="ids">The ids <c>.create</c> answered.</param>
    /// <param name="created">The records read back.</param>
    /// <param name="cancellationToken">The stop token.</param>
    private protected virtual Task<IReadOnlyList<object>> CompleteAsync(IReadOnlyList<ulong> ids, IReadOnlyList<TEntity> created, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<object>>([.. created.Cast<object>()]);

    /// <summary>The one call: <c>ZabbixClient.&lt;Entities&gt;.CreateAsync(new TRequest { ... }, cancellationToken)</c>.</summary>
    /// <param name="cancellationToken">The stop token.</param>
    protected abstract Task<IReadOnlyList<ulong>> CreateAsync(CancellationToken cancellationToken);
}
