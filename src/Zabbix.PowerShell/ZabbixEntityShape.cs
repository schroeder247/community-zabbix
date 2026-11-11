// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix.PowerShell;

/// <summary>Builds entity shapes over the generated parameter records.</summary>
internal static class ZabbixEntityShape
{
    // The API objects whose names run words together, in the words the frontend's menus use; every other API name is
    // already a word (host, item, trigger).
    private static readonly FrozenDictionary<string, string> s_nouns = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ceprule"] = "CEP rule",
        ["dcheck"] = "discovery check",
        ["dhost"] = "discovered host",
        ["discoveryrule"] = "discovery rule",
        ["discoveryruleprototype"] = "discovery rule prototype",
        ["drule"] = "network discovery rule",
        ["dservice"] = "discovered service",
        ["graphitem"] = "graph item",
        ["graphprototype"] = "graph prototype",
        ["hanode"] = "HA node",
        ["hostdashboard"] = "host dashboard",
        ["hostgroup"] = "host group",
        ["hostinterface"] = "host interface",
        ["hostprototype"] = "host prototype",
        ["httptest"] = "web scenario",
        ["iconmap"] = "icon map",
        ["itemprototype"] = "item prototype",
        ["mediatype"] = "media type",
        ["proxygroup"] = "proxy group",
        ["regexp"] = "regular expression",
        ["templatedashboard"] = "template dashboard",
        ["templategroup"] = "template group",
        ["token"] = "API token",
        ["triggerprototype"] = "trigger prototype",
        ["userdirectory"] = "user directory",
        ["usergroup"] = "user group",
        ["usermacro"] = "user macro",
        ["valuemap"] = "value map",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The API object <paramref name="api"/> as a message names it: <c>host group</c> for <c>hostgroup</c>.</summary>
    /// <param name="api">The API object's name.</param>
    public static string NounOf(string api) => s_nouns.TryGetValue(api, out string? noun) ? noun : api;

    /// <summary>
    /// A shape whose name selection is <c>{api}.get</c> with a case-insensitive exact <c>search: {nameField: names}</c>
    /// (<c>filter</c> when a name holds a <c>*</c>) and <c>output: [idField, nameField]</c>, written through
    /// <see cref="IZabbixGetParameters"/> so it holds for every generated <c>*GetParameters</c>.
    /// </summary>
    /// <typeparam name="TEntity">The read model record.</typeparam>
    /// <typeparam name="TParameters">The entity's <c>*GetParameters</c>.</typeparam>
    /// <param name="api">The API object's name.</param>
    /// <param name="idField">The id field on the wire.</param>
    /// <param name="nameField">The name field on the wire.</param>
    /// <param name="idOf">Reads the id off a record.</param>
    /// <param name="nameOf">Reads the name off a record.</param>
    /// <param name="parametersInfo">The parameters' generated JSON contract.</param>
    /// <param name="resultInfo">The result list's generated JSON contract.</param>
    /// <param name="buildIdSelection">
    /// The <c>.get</c> parameters that select by id alone: the generator supplies either
    /// <c>ids => new TParameters { &lt;EntityIds&gt; = ... }</c> (the entity's own top-level <c>*ids</c> option, when
    /// its <c>*GetParameters</c> carries one) or the <c>filter.&lt;idfield&gt;</c> fallback below, for entities with
    /// no such option.
    /// </param>
    /// <param name="materialize">
    /// Builds the entity's <c>*GetParameters</c> from a selection's common options, supplying whatever the record requires
    /// beyond them (a <c>hostdashboard.get</c> needs its hosts); generated, because only the concrete record can set a
    /// required member.
    /// </param>
    public static ZabbixEntityShape<TEntity> Create<TEntity, TParameters>(
        string api, string idField, string nameField, Func<TEntity, ulong?> idOf, Func<TEntity, string?> nameOf,
        JsonTypeInfo<TParameters> parametersInfo, JsonTypeInfo<IReadOnlyList<TEntity>> resultInfo,
        Func<IReadOnlyList<ulong>, TParameters> buildIdSelection,
        Func<ZabbixGetSelection, TParameters> materialize)
        where TParameters : IZabbixGetParameters
    {
        ArgumentNullException.ThrowIfNull(parametersInfo);
        ArgumentNullException.ThrowIfNull(resultInfo);
        ArgumentNullException.ThrowIfNull(buildIdSelection);
        ArgumentNullException.ThrowIfNull(materialize);
        string method = api + ".get";
        return new ZabbixEntityShape<TEntity>(api, idField, nameField, idOf, nameOf,
            selectByName: async (client, names, cancellationToken) =>
            {
                // Case-insensitive, as a Get- -Name is: an exact search (a pattern with no '*'
                // is exact under searchWildcardsEnabled; zbx_db_search compares UPPER() to UPPER()). A name that itself
                // holds a '*' cannot be said as a search pattern, so such a batch keeps the exact, case-sensitive filter.
                bool searchable = names.All(name => !name.Contains('*', StringComparison.Ordinal));
                var byName = new Dictionary<string, ZabbixFieldValue>(StringComparer.Ordinal) { [nameField] = names.ToArray() };
                TParameters parameters = materialize(new ZabbixGetSelection
                {
                    Output = [idField, nameField],
                    Filter = names.Count == 0 || searchable ? null : byName,
                    Search = names.Count == 0 || !searchable ? null : byName,
                    SearchWildcardsEnabled = names.Count > 0 && searchable ? true : null,
                    SearchByAny = names.Count > 0 && searchable ? true : null,
                });
                return await client.SendAsync(method, parameters, parametersInfo, resultInfo, cancellationToken).ConfigureAwait(false);
            },
            selectById: async (client, ids, cancellationToken) =>
            {
                // No Output restriction (unlike selectByName's [idField, nameField]): the full default field set, the
                // same as ZabbixGetCmdlet<TEntity,TParameters>.Selection()'s ById case already sends for Get-Zbx<Entity> -Id.
                // buildIdSelection, not filter.<idfield> unconditionally: some objects' .get rejects a
                // filter-based id lookup outright (template.get {"filter":{"templateid":[...]}} answers [] even for a
                // real id) but does accept its own top-level *ids option (templateids).
                TParameters parameters = buildIdSelection(ids);
                return await client.SendAsync(method, parameters, parametersInfo, resultInfo, cancellationToken).ConfigureAwait(false);
            },
            buildIdSelection: ids => buildIdSelection(ids),
            selectStartingWith: async (client, text, limit, cancellationToken) =>
            {
                // startSearch anchors the search at the start of the name; zbx_db_search compares UPPER() to UPPER(), so
                // it is case-insensitive as -Name is; a '*' is the API's own wildcard. The answer is bounded by limit.
                TParameters parameters = materialize(new ZabbixGetSelection
                {
                    Output = [idField, nameField],
                    Search = text.Length == 0 ? null : new Dictionary<string, ZabbixFieldValue>(StringComparer.Ordinal) { [nameField] = new[] { text } },
                    StartSearch = text.Length == 0 ? null : true,
                    SearchWildcardsEnabled = text.Contains('*', StringComparison.Ordinal) ? true : null,
                    Limit = limit,
                });
                return await client.SendAsync(method, parameters, parametersInfo, resultInfo, cancellationToken).ConfigureAwait(false);
            },
            materialize: selection => materialize(selection));
    }
}
