// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Describes what the module needs to know about one API object to turn a name or a piped object into its id and to
/// complete names: the API's name for it (<c>host</c>), its id and name fields on the wire (<c>hostid</c>, <c>host</c>),
/// how to read them off a record, and one <c>.get</c> selecting by name. The generator emits one per entity into
/// <see cref="ZabbixEntities"/>; the selection goes through the client's raw call,
/// <see cref="ZabbixClient.SendAsync{TParameters, TResult}(ZabbixApiMethod, TParameters, JsonTypeInfo{TParameters}, JsonTypeInfo{TResult}, CancellationToken)"/>,
/// with the SDK's JSON contract, as an application calls a method no generated client names.
/// </summary>
/// <typeparam name="TEntity">The read model record.</typeparam>
internal sealed class ZabbixEntityShape<TEntity>
{
    private readonly Func<ZabbixClient, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<TEntity>>> _selectByName;
    private readonly Func<ZabbixClient, IReadOnlyList<ulong>, CancellationToken, Task<IReadOnlyList<TEntity>>> _selectById;
    private readonly Func<ZabbixClient, string, int, CancellationToken, Task<IReadOnlyList<TEntity>>> _selectStartingWith;
    private readonly Func<IReadOnlyList<ulong>, IZabbixGetParameters> _buildIdSelection;
    private readonly Func<ZabbixGetSelection, IZabbixGetParameters> _materialize;

    /// <summary>The shape of <typeparamref name="TEntity"/>.</summary>
    /// <param name="api">The API object's name, the method prefix (<c>host</c> for <c>host.get</c>).</param>
    /// <param name="idField">The id field on the wire.</param>
    /// <param name="nameField">The field a user names the object by.</param>
    /// <param name="idOf">Reads the id off a record.</param>
    /// <param name="nameOf">Reads the name off a record.</param>
    /// <param name="selectByName">One <c>.get</c> returning the objects whose <paramref name="nameField"/> is any of the names, with at least the id and name fields.</param>
    /// <param name="selectById">One <c>.get</c> returning the full objects whose <paramref name="idField"/> is any of the ids (every field the API answers by default — the same shape <c>Get-Zbx&lt;Entity&gt; -Id</c> returns).</param>
    /// <param name="buildIdSelection">
    /// The <c>.get</c> parameters that select by id alone: the entity's own top-level <c>*ids</c> option (e.g.
    /// <c>templateids</c>) when its <c>*GetParameters</c> carries one, else <c>filter.&lt;idfield&gt;</c>. Some objects'
    /// <c>.get</c> ignores the filter form (<c>template.get {"filter":{"templateid":[...]}}</c> answers <c>[]</c> even
    /// for a real id).
    /// </param>
    /// <param name="selectStartingWith">One bounded <c>.get</c> returning at most a number of objects whose name starts with a text (tab completion).</param>
    /// <param name="materialize">Builds the entity's <c>*GetParameters</c> from a selection's common options, supplying what the record requires beyond them.</param>
    public ZabbixEntityShape(
        string api,
        string idField,
        string nameField,
        Func<TEntity, ulong?> idOf,
        Func<TEntity, string?> nameOf,
        Func<ZabbixClient, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<TEntity>>> selectByName,
        Func<ZabbixClient, IReadOnlyList<ulong>, CancellationToken, Task<IReadOnlyList<TEntity>>> selectById,
        Func<IReadOnlyList<ulong>, IZabbixGetParameters> buildIdSelection,
        Func<ZabbixClient, string, int, CancellationToken, Task<IReadOnlyList<TEntity>>> selectStartingWith,
        Func<ZabbixGetSelection, IZabbixGetParameters> materialize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(api);
        ArgumentException.ThrowIfNullOrWhiteSpace(idField);
        ArgumentException.ThrowIfNullOrWhiteSpace(nameField);
        ArgumentNullException.ThrowIfNull(idOf);
        ArgumentNullException.ThrowIfNull(nameOf);
        ArgumentNullException.ThrowIfNull(selectByName);
        ArgumentNullException.ThrowIfNull(selectById);
        ArgumentNullException.ThrowIfNull(buildIdSelection);
        ArgumentNullException.ThrowIfNull(selectStartingWith);
        ArgumentNullException.ThrowIfNull(materialize);
        Api = api;
        IdField = idField;
        NameField = nameField;
        IdOf = idOf;
        NameOf = nameOf;
        _selectByName = selectByName;
        _selectById = selectById;
        _buildIdSelection = buildIdSelection;
        _selectStartingWith = selectStartingWith;
        _materialize = materialize;
    }

    /// <summary>The API object's name (<c>host</c>).</summary>
    public string Api { get; }

    /// <summary>The object as a message names it (<c>host group</c> for <c>hostgroup</c>): <see cref="ZabbixEntityShape.NounOf"/>.</summary>
    public string Noun => ZabbixEntityShape.NounOf(Api);

    /// <summary>The id field on the wire (<c>hostid</c>).</summary>
    public string IdField { get; }

    /// <summary>The field a user names the object by (<c>host</c>).</summary>
    public string NameField { get; }

    /// <summary>Reads the id off a record.</summary>
    public Func<TEntity, ulong?> IdOf { get; }

    /// <summary>Reads the name off a record.</summary>
    public Func<TEntity, string?> NameOf { get; }

    /// <summary>The objects named any of <paramref name="names"/> (every object when the list is empty).</summary>
    /// <param name="client">The server's client.</param>
    /// <param name="names">The names; empty for all.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<IReadOnlyList<TEntity>> SelectByNameAsync(ZabbixClient client, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(names);
        return _selectByName(client, names, cancellationToken);
    }

    /// <summary>
    /// At most <paramref name="limit"/> objects whose name starts with <paramref name="text"/>, case-insensitively (a
    /// <c>*</c> in it is a wildcard), with the id and name fields only: what tab completion asks for.
    /// </summary>
    /// <param name="client">The server's client.</param>
    /// <param name="text">The start of the name; empty for any.</param>
    /// <param name="limit">The most objects answered.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<IReadOnlyList<TEntity>> SelectStartingWithAsync(ZabbixClient client, string text, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(text);
        return _selectStartingWith(client, text, limit, cancellationToken);
    }

    /// <summary>
    /// The full objects named any of <paramref name="ids"/>, with every field the API answers by default, the same as
    /// <c>Get-Zbx&lt;Entity&gt; -Id</c>. <c>New-Zbx&lt;Entity&gt;</c> and <c>Set-Zbx&lt;Entity&gt;</c> read back through it,
    /// in one call for the whole batch, so they return the created or updated entity rather than a bare id.
    /// </summary>
    /// <param name="client">The server's client.</param>
    /// <param name="ids">The ids.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<IReadOnlyList<TEntity>> SelectByIdAsync(ZabbixClient client, IReadOnlyList<ulong> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(ids);
        return _selectById(client, ids, cancellationToken);
    }

    /// <summary>
    /// The <c>.get</c> parameters that select this entity by id alone: the entity's own top-level
    /// <c>*ids</c> option when its <c>*GetParameters</c> carries one, else <c>filter.&lt;idfield&gt;</c>. Used by
    /// <c>ZabbixGetCmdlet&lt;TEntity,TParameters&gt;.Selection()</c>'s <c>-Id</c> case, cast back to the concrete
    /// <c>TParameters</c> the delegate <see cref="ZabbixEntityShape.Create{TEntity,TParameters}"/> built this shape
    /// from — always safe, since that delegate only ever returns an instance of exactly that type.
    /// </summary>
    /// <param name="ids">The ids.</param>
    public IZabbixGetParameters BuildIdSelection(IReadOnlyList<ulong> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return _buildIdSelection(ids);
    }

    /// <summary>
    /// The entity's <c>get</c> parameters carrying <paramref name="selection"/>'s common options, built by the record's
    /// generated factory, which supplies whatever the record requires beyond them.
    /// </summary>
    /// <param name="selection">The common options to carry.</param>
    /// <returns>The parameters record, an instance of the entity's own <c>*GetParameters</c> type.</returns>
    public IZabbixGetParameters Materialize(ZabbixGetSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return _materialize(selection);
    }
}
