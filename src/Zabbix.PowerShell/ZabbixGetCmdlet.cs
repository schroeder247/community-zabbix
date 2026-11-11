// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Reflection;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Provides the base of every <c>Get-Zbx&lt;Entity&gt;</c> cmdlet (the generator emits the derived classes). Four
/// parameter sets, one <c>.get</c>: <c>-Id</c>, <c>-Name</c> (exact, or a search when it holds <c>*</c>), <c>-Filter</c>
/// taking the entity's typed <typeparamref name="TParameters"/>, or nothing. The derived class supplies the
/// <c>-Name</c> parameter (its completer is keyed on the entity, and an attribute cannot take a type parameter) and the
/// one call.
/// </summary>
/// <typeparam name="TEntity">The read model.</typeparam>
/// <typeparam name="TParameters">The entity's <c>*GetParameters</c>.</typeparam>
public abstract class ZabbixGetCmdlet<TEntity, TParameters> : ZabbixServerCmdlet
    where TParameters : IZabbixGetParameters
{
    /// <summary>The objects' names; the derived class declares it with <c>[Parameter(Mandatory = true, Position = 0, ParameterSetName = ByName)]</c> and <c>[ZabbixCompleter&lt;TEntity&gt;]</c>.</summary>
    public abstract string[]? Name { get; set; }

    /// <summary>
    /// The objects' ids. Abstract for the same reason <see cref="Name"/> is: the derived class declares it with
    /// <c>[Parameter(Mandatory = true, ParameterSetName = ById, ValueFromPipelineByPropertyName = true)]</c> and the
    /// entity's own id member as an alias (<c>HostId</c> on <c>Get-ZbxHost</c>), so piped objects carrying the id
    /// (<c>[pscustomobject]@{ HostId = '10084' } | Get-ZbxHost</c>) bind; a piped id itself is <see cref="InputObject"/>.
    /// </summary>
    public abstract ulong[]? Id { get; set; }

    /// <summary>
    /// A piped id or name, as <c>Get-Service</c> takes a piped name: a decimal id selects by id
    /// (<c>@('10084','10085') | Get-ZbxHost</c>), any other text by name (<c>'web-*' | Get-ZbxHost</c>), and a record
    /// that binds no other parameter by property name selects by its <c>Name</c> (<c>Import-Csv</c> with a <c>Name</c>
    /// column). A record that binds another parameter (its <c>HostId</c>, its <c>GroupId</c>) is read through those.
    /// </summary>
    /// <remarks>
    /// Its own parameter, in every set, rather than <c>ValueFromPipeline</c> on <c>-Name</c> and <c>-Id</c>: the binder
    /// tries a piped value by value with conversion before it tries properties with conversion, so a by-value
    /// <c>string[]</c> would take any record as its text (<c>@{HostId=7}</c>) ahead of the record's own
    /// <c>HostId</c>, and two by-value parameters in two sets leave a piped id no set to choose.
    /// </remarks>
    [Parameter(ValueFromPipeline = true, DontShow = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>
    /// The full selection, as the entity's typed <c>.get</c> parameters. Abstract for the same reason <see cref="Name"/>
    /// is (an attribute cannot take a type parameter): the derived class declares it with
    /// <c>[Parameter(Mandatory = true, ParameterSetName = ByFilter)]</c> and no transformation attribute: a bare
    /// <c>-Filter @{ hostid = '1' }</c> hashtable binds through PowerShell's own parameter binder, which dispatches to
    /// <see cref="ZabbixRecordConverter"/> (registered for every <c>*GetParameters</c> in the generated
    /// <c>Community.Zabbix.types.ps1xml</c>) and so remaps its keys to wire names the same way <c>-AdditionalProperty</c> does.
    /// </summary>
    public abstract TParameters? Filter { get; set; }

    /// <summary>
    /// At most this many objects. Abstract for the reason <see cref="Name"/> is: the derived class declares it in the
    /// <c>All</c> set, and in <c>ByName</c> only when the entity has a name, so a name-less entity (history, trends,
    /// alerts) has no <c>ByName</c> set that differs from <c>All</c> by nothing.
    /// </summary>
    public abstract int? Limit { get; set; }

    // Eight of the eleven options IZabbixGetParameters guarantees every *GetParameters carries (CommonGetOptions,
    // ZabbixApiEmitter) — all but Output/Filter (already ByFilter's whole -Filter) and Limit (above): sort, search,
    // and count-instead-of-objects. Declared with no ParameterSetName, so they compose with every one of -Id/-Name/
    // -Filter/no-selection at once (all fold into the same request object -Filter builds) rather than gating a fifth
    // exclusive set.

    /// <summary>
    /// Sort by these fields, in order (<c>sortfield</c>). Abstract for the reason <see cref="Name"/> is: the derived class
    /// declares it with <c>[Parameter]</c> and <c>[ArgumentCompletions]</c> naming the fields <typeparamref name="TParameters"/>
    /// sorts by (its <c>SortableFields</c>), so Tab completes them rather than file names.
    /// </summary>
    public abstract string[]? SortField { get; set; }

    /// <summary>
    /// Ascending or descending, one per <c>-SortField</c> field (<c>sortorder</c>); a list shorter than
    /// <c>-SortField</c> leaves the rest at the API's own default (ascending).
    /// </summary>
    [Parameter]
    public ZabbixSortOrder[]? SortOrder { get; set; }

    /// <summary>Substring matches (<c>search</c>): a hashtable of wire field name to a substring or an array of substrings.</summary>
    [Parameter]
    public Hashtable? Search { get; set; }

    /// <summary>The <c>-Search</c> terms are OR-ed (<c>searchByAny</c>); the API's own default is AND.</summary>
    [Parameter]
    public SwitchParameter SearchByAny { get; set; }

    /// <summary><c>-Search</c> matches only at the start of the field (<c>startSearch</c>).</summary>
    [Parameter]
    public SwitchParameter StartSearch { get; set; }

    /// <summary>Return what does <em>not</em> match <c>-Search</c> (<c>excludeSearch</c>).</summary>
    [Parameter]
    public SwitchParameter ExcludeSearch { get; set; }

    /// <summary><c>*</c> in a <c>-Search</c> term is a wildcard (<c>searchWildcardsEnabled</c>).</summary>
    [Parameter]
    public SwitchParameter SearchWildcardsEnabled { get; set; }

    /// <summary>Writes the number of matching objects instead of the objects themselves (<c>countOutput</c>).</summary>
    /// <remarks>
    /// A plain <see cref="SwitchParameter"/>, not a <c>ZabbixOutput</c>-shaped value: <c>countOutput</c> is a plain
    /// <c>apibool</c> on the wire, not an <c>output</c> field, so unlike <c>-Select*</c> there is no field-list form to
    /// accept.
    /// </remarks>
    [Parameter]
    public SwitchParameter CountOutput { get; set; }

    /// <summary>
    /// <see cref="SortOrder"/>, converted to the SDK's <see cref="System.ComponentModel.ListSortDirection"/>, or <see langword="null"/>
    /// when not bound. A plain helper, not folded here: <c>with</c> (the only way to change one member of an existing
    /// record instance without losing every other member <see cref="Selection"/> or <c>-Filter</c> already set) needs
    /// a compile-time record type, which the generic <typeparamref name="TParameters"/> is not (CS8858,
    /// "the receiver type 'TParameters' is not a valid record type") even though every <c>*GetParameters</c> record
    /// it stands for is one. <see cref="ApplyEntityFilters"/> — overridden where <typeparamref name="TParameters"/>
    /// is concrete again — calls this and folds the result with <c>with</c>.
    /// </summary>
    protected System.ComponentModel.ListSortDirection[]? SortOrderWire() => SortOrder is { Length: > 0 } ? [.. SortOrder.Select(o => o.ToDirection())] : null;

    /// <summary>The bound <see cref="Search"/> hashtable as the wire's own field-name/value dictionary, or <see langword="null"/> when not bound.</summary>
    protected IReadOnlyDictionary<string, ZabbixFieldValue>? SearchDictionary()
    {
        if (Search is not { Count: > 0 }) return null;
        var search = new Dictionary<string, ZabbixFieldValue>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Search)
        {
            string key = entry.Key.ToString() ?? string.Empty;
            search[key] = ToFieldValue(key, entry.Value);
        }
        return search;
    }

    // A search term is text: a string or a number (written invariantly), or a flat list of them — the "one or
    // several" the wire's search grammar takes. Anything else (a nested list, a hashtable, a date, an object) has no
    // text the caller wrote and would be sent as its type name ("System.Object[]"), so it is refused, naming the key.
    private static ZabbixFieldValue ToFieldValue(string key, object? value)
    {
        object? given = value is PSObject wrapped ? wrapped.BaseObject : value;
        if (given is null) return string.Empty;
        if (Term(given) is { } single) return single;
        if (given is IEnumerable list and not IDictionary)
        {
            var terms = new List<string>();
            foreach (object? item in list)
                terms.Add(Term(item is PSObject inner ? inner.BaseObject : item) ?? throw Refused(key, item));
            return terms.ToArray();
        }
        throw Refused(key, given);
    }

    private static string? Term(object? value) => value switch
    {
        string text => text,
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal or double or float => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    private static ZabbixParameterException Refused(string key, object? value)
        => new("ZabbixSearchValueInvalid", nameof(Search),
            $"-Search '{key}': a term is a string, a number or a flat list of them, not {value?.GetType().Name ?? "null"}");

    /// <summary>
    /// Folds every bound option that only <c>with</c> on a concrete record can apply without losing whatever
    /// <see cref="Selection"/> or <c>-Filter</c> already set: the eight common options this class declares besides <see cref="Limit"/>
    /// (<see cref="SortField"/>/<see cref="SortOrder"/>/<see cref="Search"/>/<see cref="SearchByAny"/>/
    /// <see cref="StartSearch"/>/<see cref="ExcludeSearch"/>/<see cref="SearchWildcardsEnabled"/>/
    /// <see cref="CountOutput"/> — composing with every one of <c>-Id</c>/<c>-Name</c>/<c>-Filter</c>/no-selection
    /// alike) and, when the entity has any, its own <c>-Select*</c>/<c>-Editable</c> (not on
    /// <see cref="IZabbixGetParameters"/> — mined per entity instead). The base folds nothing; every generated
    /// <c>Get-</c> cmdlet and the hand-written <c>Get-ZbxHost</c> override it.
    /// </summary>
    /// <param name="parameters">The selection so far.</param>
    protected virtual TParameters ApplyEntityFilters(TParameters parameters) => parameters;

    /// <summary>The <c>.get</c> parameters the bound parameter set selects.</summary>
    protected TParameters Selection()
    {
        ZabbixEntityShape<TEntity> shape = ZabbixEntities.Of<TEntity>();
        _namesHere = null;
        _unfound = null;
        if (PipedSelection(shape) is { } piped) return piped;
        return ParameterSetName switch
        {
            ZabbixParameterSets.ByFilter => Filter!,
            // shape.BuildIdSelection, not always Filter[shape.IdField] — some objects' .get rejects a filter-based id
            // lookup outright (template.get {"filter":{"templateid":[...]}} answers [] even for a real id) but accepts
            // their own top-level *ids option (templateids), which the shape's delegate (generator-supplied,
            // ZabbixCmdletEmitter.EmitEntities) uses when the entity has one. The cast
            // is safe: the delegate this shape was built from (ZabbixEntityShape.Create<TEntity,TParameters>) only
            // ever returns an instance of exactly this TParameters.
            // Id 0 is Zabbix's "none" (a graph's or a host's templateid when it has none), the same rule the reference
            // parameters apply (ZabbixReferenceAttribute): an object piped for another parameter binds its own
            // TemplateId "0" to Get-ZbxTemplate -Id by property name, and templateids: [0] would empty the
            // result. Only zeros means no id selection at all.
            ZabbixParameterSets.ById when Id!.All(id => id == 0) => (TParameters)shape.Materialize(new ZabbixGetSelection { Limit = Limit }),
            ZabbixParameterSets.ById => (TParameters)shape.BuildIdSelection([.. Id!.Where(id => id != 0)]),
            ZabbixParameterSets.ByName => NameSelection(shape, Name!),
            // A piped record's own id, bound by property name in the default set (the derived -Id's second use).
            _ when Id is { Length: > 0 } && Id.Any(id => id != 0) => (TParameters)shape.BuildIdSelection([.. Id.Where(id => id != 0)]),
            _ => (TParameters)shape.Materialize(new ZabbixGetSelection { Limit = Limit }),
        };
    }

    // The parameters written on the command line: what the pipeline binds is what a record adds to them.
    private HashSet<string>? _written;

    /// <inheritdoc/>
    protected override void BeginProcessing()
    {
        _written = [.. MyInvocation.BoundParameters.Keys];
        // The engine's paging (-First, -Skip, -IncludeTotalCount): -First is the server's limit, as -Limit is, with the
        // objects -Skip passes over on top; -Skip is applied here, to what is written.
        if (Limit is null && PagingParameters.First != ulong.MaxValue)
            Limit = (int)Math.Min(int.MaxValue, Math.Max(1UL, PagingParameters.First) + Math.Min(PagingParameters.Skip, int.MaxValue));
        _skipLeft = PagingParameters.Skip;
        base.BeginProcessing();
    }

    // The objects -Skip still passes over.
    private ulong _skipLeft;

    // The reference filters this record bound by a property it only shares the name of (DropForeignReferences).
    private HashSet<string> _foreign = [];

    /// <summary>
    /// Unbinds the reference filters a piped Zabbix record of another object bound by property name, and returns their
    /// names. The binder matches a property by its name alone, and the same name means different things on different
    /// records: an item's <c>TemplateId</c> is the template item it was inherited from, not a template, and a host's
    /// <c>ProxyId</c> is not a filter on its items. A reference filter takes a piped record's id only from a record of
    /// the filter's own object (<c>Get-ZbxHost | Get-ZbxItem</c>); a record that is not a Zabbix read record (a CSV
    /// row, a <c>[pscustomobject]</c>) names its references as the user wrote them, and binds them all.
    /// </summary>
    private HashSet<string> DropForeignReferences()
    {
        if (InputObject?.BaseObject is not { } piped || !ZabbixEntities.IsEntity(piped.GetType())) return [];
        HashSet<string> dropped = [];
        foreach (string key in MyInvocation.BoundParameters.Keys)
        {
            if (_written?.Contains(key) == true) continue;
            PropertyInfo? property = GetType().GetProperty(key);
            if (property?.PropertyType is { IsArray: true } type && type.GetElementType() is { IsGenericType: true } element
                && element.GetGenericTypeDefinition() == typeof(ZabbixReference<>) && element.GetGenericArguments()[0] != piped.GetType())
            {
                property.SetValue(this, null);
                dropped.Add(key);
            }
        }
        return dropped;
    }

    /// <summary>
    /// The selection a piped <see cref="InputObject"/> makes, or <see langword="null"/> when there is none or the record
    /// bound other parameters by property name (the parameter set reads those).
    /// </summary>
    /// <exception cref="ParameterBindingException">The piped object is neither an id nor a name and binds nothing.</exception>
    private TParameters? PipedSelection(ZabbixEntityShape<TEntity> shape)
    {
        object? piped = InputObject?.BaseObject;
        if (piped is null) return default;
        bool named = MyInvocation.MyCommand.Parameters.ContainsKey(nameof(Name));
        string? text = piped switch
        {
            string value => value,
            ulong id => id.ToString(CultureInfo.InvariantCulture),
            byte or ushort or uint or ulong or sbyte or short or int or long => Convert.ToString(piped, CultureInfo.InvariantCulture),
            _ => null,
        };
        if (text is null)
        {
            // A record: what it bound by property name selects; one that bound nothing selects by its Name.
            if (MyInvocation.BoundParameters.Keys.Any(key => key != nameof(InputObject) && _written?.Contains(key) != true && !_foreign.Contains(key))) return default;
            text = named ? InputObject!.Properties["Name"]?.Value?.ToString() : null;
            if (text is null)
                throw new ParameterBindingException($"The piped object binds no parameter: it is not an id or a name, and it carries no {shape.Api} id{(named ? " or name" : string.Empty)}.");
            return NameSelection(shape, [text]);
        }
        if (ulong.TryParse(text, CultureInfo.InvariantCulture, out ulong parsed))
            return parsed == 0 ? (TParameters)shape.Materialize(new ZabbixGetSelection { Limit = Limit }) : (TParameters)shape.BuildIdSelection([parsed]);
        if (!named)
            throw new ParameterBindingException($"'{text}' is not a {shape.Api} id, and a {shape.Api} has no name to select by.");
        return NameSelection(shape, [text]);
    }

    private ZabbixNamePattern[]? _namesHere;
    private int _admitted;

    // The -Name values written without a wildcard that no object written so far has matched (Get-Service's rule: a
    // name that finds nothing is an ObjectNotFound error, a pattern that finds nothing is not).
    private List<(string Written, ZabbixNamePattern Pattern)>? _unfound;

    /// <summary>
    /// <c>-Name</c> as a selection: every name a PowerShell wildcard pattern
    /// (<see cref="ZabbixNamePattern"/>), sent as one case-insensitive <c>search</c> on the entity's name field with
    /// <c>searchWildcardsEnabled</c> and <c>searchByAny</c> (<c>zbx_db_search</c> joins a field's patterns with the same
    /// glue as its fields). What the server cannot say — a <c>?</c>, an escaped <c>`*</c>, or any name next to a
    /// caller's own <c>-Search</c>, which needs the fields AND-ed — is matched here instead (<see cref="Admits"/>), and
    /// <c>-Limit</c> then counts what is admitted rather than what the server sent.
    /// </summary>
    private TParameters NameSelection(ZabbixEntityShape<TEntity> shape, string[] names)
    {
        ZabbixNamePattern[] patterns = [.. names.Select(ZabbixNamePattern.Parse)];
        _unfound = [.. names.Zip(patterns).Where(name => !name.Second.HasWildcard)];
        bool callerSearch = Search is { Count: > 0 };
        if (callerSearch || patterns.Any(p => !p.Precise)) _namesHere = patterns;
        if (callerSearch) return (TParameters)shape.Materialize(new ZabbixGetSelection());
        return (TParameters)shape.Materialize(new ZabbixGetSelection
        {
            Search = new Dictionary<string, ZabbixFieldValue>(StringComparer.Ordinal) { [shape.NameField] = patterns.Select(p => p.Server).ToArray() },
            SearchWildcardsEnabled = true,
            SearchByAny = true,
            Limit = _namesHere is null ? Limit : null,
        });
    }

    /// <inheritdoc/>
    private protected override bool Admits(object? item)
    {
        if (item is not TEntity entity) return true;
        if (_namesHere is not null || _unfound is { Count: > 0 })
        {
            string? name = ZabbixEntities.Of<TEntity>().NameOf(entity);
            if (_namesHere is not null)
            {
                if (!_namesHere.Any(pattern => pattern.IsMatch(name))) return false;
                if (Limit is { } limit && ++_admitted > limit) return false;
            }
            _unfound?.RemoveAll(unfound => unfound.Pattern.IsMatch(name));
        }
        if (_skipLeft == 0) return true;
        _skipLeft--;
        return false;
    }

    /// <summary>The engine's error id for a piped object that binds no parameter (<c>ParameterBinderStrings.InputObjectNotBound</c>).</summary>
    internal const string EngineInputNotBound = ZabbixPipedRecord.EngineInputNotBound;

    // The ids piped records selected by, read together (TryBuffer).
    private readonly List<ulong> _pipedIds = [];

    /// <summary>How many piped ids one <c>.get</c> reads at most.</summary>
    internal const int PipedIdBatch = 100;

    /// <summary>
    /// Holds a piped record's id for one <c>.get</c> with the ids of the records around it, as <c>Set-</c> and
    /// <c>Remove-</c> batch theirs: <c>.get</c> takes a list of ids, and twenty piped ids would otherwise be twenty calls. Only a record
    /// that differs from the others by its id alone is held: one that bound anything else by property name (a history
    /// read's <c>ValueType</c>, a reference filter) reads on its own, and so does a count or a limited read, whose
    /// answer is per record.
    /// </summary>
    private bool TryBuffer()
    {
        if (Limit is not null || CountOutput.IsPresent || PagingParameters.IncludeTotalCount.IsPresent || _written is null) return false;
        if (_written.Contains(nameof(Id)) || _written.Contains(nameof(Name)) || _written.Contains(nameof(Filter))) return false;
        if (MyInvocation.BoundParameters.Keys.Any(key => key is not (nameof(Id) or nameof(InputObject)) && !_written.Contains(key) && !_foreign.Contains(key))) return false;
        ulong[] ids;
        if (MyInvocation.BoundParameters.ContainsKey(nameof(Id)) && Id is { Length: > 0 })
        {
            ids = [.. Id.Where(id => id != 0)];
        }
        else
        {
            object? piped = InputObject?.BaseObject;
            string? text = piped switch
            {
                string value => value,
                ulong id => id.ToString(CultureInfo.InvariantCulture),
                byte or ushort or uint or ulong or sbyte or short or int or long => Convert.ToString(piped, CultureInfo.InvariantCulture),
                _ => null,
            };
            if (!ulong.TryParse(text, CultureInfo.InvariantCulture, out ulong parsed)) return false;
            ids = [parsed];
        }
        if (ids.Length == 0) return false;
        _pipedIds.AddRange(ids);
        if (_pipedIds.Count >= PipedIdBatch) ReadPipedIds();
        return true;
    }

    // One .get for the held ids, the command line's other parameters applied as each record's would have been.
    private void ReadPipedIds()
    {
        if (_pipedIds.Count == 0) return;
        ulong[] ids = [.. _pipedIds.Distinct()];
        _pipedIds.Clear();
        _namesHere = null;
        _unfound = null;
        TParameters parameters = ApplyEntityFilters((TParameters)ZabbixEntities.Of<TEntity>().BuildIdSelection(ids));
        if (HasReferenceFilters)
        {
            if (!TryRun(cancellationToken => ApplyReferenceFiltersAsync(parameters, cancellationToken), out TParameters? refined, parameters)) return;
            parameters = refined;
        }
        Query(ZabbixClient, parameters);
    }

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        ReadPipedIds();
        base.EndProcessing();
    }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        TParameters parameters;
        _foreign = DropForeignReferences();
        if (TryBuffer()) return;
        // A record read on its own reads after the ones held before it, so the output keeps the input's order.
        ReadPipedIds();
        try
        {
            parameters = ApplyEntityFilters(Selection());
        }
        catch (ZabbixParameterException refused)
        {
            ThrowTerminatingError(ZabbixErrors.From(refused, Search));
            return;
        }
        catch (ParameterBindingException unbound)
        {
            // One piped record that selects nothing: its error, and the next record. The engine's own id: InputObject
            // takes what the binder would have refused, so the refusal is the binder's, as on every other command.
            WriteError(new ErrorRecord(unbound, EngineInputNotBound, ErrorCategory.InvalidArgument, InputObject));
            return;
        }
        if (_namesHere is not null && parameters.CountOutput == true)
        {
            ThrowTerminatingError(new ErrorRecord(
                new PSArgumentException("-CountOutput counts on the server, which cannot match this -Name (a '?', an escaped '`*', or -Name with -Search); count the objects instead: (Get-... | Measure-Object).Count", nameof(CountOutput)),
                "ZabbixCountOutputNameNotServerSide", ErrorCategory.InvalidArgument, Name));
        }
        if (HasReferenceFilters)
        {
            if (!TryRun(cancellationToken => ApplyReferenceFiltersAsync(parameters, cancellationToken), out TParameters? refined, parameters)) return;
            parameters = refined;
        }
        Query(ZabbixClient, parameters);
        // -IncludeTotalCount: the count of the same selection, before -First and -Skip, as the engine's paging writes it.
        if (PagingParameters.IncludeTotalCount.IsPresent && parameters.CountOutput != true && !LastRunFailed
            && TryRun(cancellationToken => CountAsync(ZabbixClient, parameters, cancellationToken), out long total, parameters))
        {
            WriteObject(PagingParameters.NewTotalCount((ulong)total, 1.0));
        }
        // A name with no wildcard that matched nothing is ObjectNotFound, non-terminating, once per name — unless the
        // read failed (its error says why) or counted, or -Limit may have cut the match off.
        if (LastRunFailed || parameters.CountOutput == true || Limit is not null) return;
        string noun = ZabbixEntities.Of<TEntity>().Noun;
        foreach ((string written, _) in _unfound ?? [])
        {
            WriteError(new ErrorRecord(new ItemNotFoundException($"no {noun} named '{written}' was found on {Connected.Name}"),
                "ZabbixNameNotFound", ErrorCategory.ObjectNotFound, written));
        }
    }

    /// <summary>The one call: <c>WriteAll(cancellationToken =&gt; client.&lt;Entities&gt;.GetAsync(parameters, cancellationToken), parameters)</c>.</summary>
    /// <param name="client">The server.</param>
    /// <param name="parameters">The selection.</param>
    protected abstract void Query(ZabbixClient client, TParameters parameters);

    /// <summary>
    /// Counts the objects <paramref name="parameters"/> selects (<c>countOutput</c>), for <c>-IncludeTotalCount</c>:
    /// <c>client.&lt;Entities&gt;.CountAsync(parameters, cancellationToken)</c>.
    /// </summary>
    /// <param name="client">The server.</param>
    /// <param name="parameters">The selection.</param>
    /// <param name="cancellationToken">The operation's token.</param>
    protected abstract Task<long> CountAsync(ZabbixClient client, TParameters parameters, CancellationToken cancellationToken);

    /// <summary>
    /// Whether any reference filter parameter (<c>item.get</c>'s <c>hostids</c>, <c>groupids</c>, ...) is bound — gates the extra resolution round
    /// trip in <see cref="ProcessRecord"/> so an ordinary call with none of them bound pays no extra <c>.get</c> and no
    /// second telemetry span. The base answers <see langword="false"/>; a generated <c>Get-</c> cmdlet with at least
    /// one reference filter parameter overrides it (and <see cref="ApplyReferenceFiltersAsync"/>) together.
    /// </summary>
    protected virtual bool HasReferenceFilters => false;

    /// <summary>
    /// Resolves this command's reference filter parameters into the matching <c>*Ids</c> member
    /// of <paramref name="parameters"/> — one <see cref="ZabbixServerCmdlet.ResolveAsync{TEntity}"/> per distinct
    /// referenced entity type, before <see cref="Query"/> runs, so <c>-Filter</c> and a reference filter parameter
    /// compose (the reference parameters set the ids options this fills in; <c>-Filter</c>, still <see cref="Filter"/>
    /// itself, carries everything else). <typeparamref name="TParameters"/> is a record with <c>init</c> members (the
    /// same shape <see cref="Selection"/> builds with an object initializer), so each bound filter is folded in with
    /// <c>with</c> rather than a property set. Only called when <see cref="HasReferenceFilters"/> is
    /// <see langword="true"/>. The base returns <paramref name="parameters"/> unchanged; a generated <c>Get-</c>
    /// cmdlet with a reference filter parameter overrides it.
    /// </summary>
    /// <param name="parameters">The selection <see cref="Query"/> will run.</param>
    /// <param name="cancellationToken">The operation's token.</param>
    protected virtual Task<TParameters> ApplyReferenceFiltersAsync(TParameters parameters, CancellationToken cancellationToken) => Task.FromResult(parameters);
}
