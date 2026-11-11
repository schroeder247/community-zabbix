// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text;
using System.Text.RegularExpressions;

namespace Community.Zabbix.Generator;

/// <summary>
/// Emits the PowerShell module's CRUD cmdlets from the API description, beside
/// <see cref="ZabbixApiEmitter"/> (which it does not change): every API object with a <c>read</c> model and a
/// <c>get</c> parameters record becomes an entry in the generated <c>ZabbixEntities</c> table; every object whose
/// service has <c>GetAsync</c>/<c>CreateAsync</c>/<c>UpdateAsync</c>/<c>DeleteAsync</c> becomes
/// <c>Get</c>/<c>New</c>/<c>Set</c>/<c>Remove</c>-<c>Zbx&lt;Entity&gt;</c> on the bases in
/// <c>Zabbix.PowerShell/ZabbixGetCmdlet.cs</c> and its siblings; each nested request record the description's
/// <c>cmdlets.builders</c> names becomes <c>New-Zbx&lt;Concept&gt;</c>, returning a <see cref="System.Collections.Hashtable"/>
/// keyed by the record's own member names, so it casts onto any owner's differently-typed nested record (host, item,
/// trigger, … each has its own <c>*TagRequest</c> with the same shape), which a typed record could not do. No field is
/// guessed: an unresolvable field kind is a diagnostic, not a silent <c>JsonElement</c>.
/// </summary>
/// <param name="api">The description.</param>
internal sealed partial class ZabbixCmdletEmitter(ApiDescription api)
{
    private readonly Dictionary<string, TypeDescription> _types = api.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
    private readonly ILookup<(string? Object, string Role), TypeDescription> _byObjectRole = api.Types.ToLookup(t => (t.Object, t.Role));
    private readonly Dictionary<string, EnumDescription> _enums = api.Enums.ToDictionary(e => e.Name, StringComparer.Ordinal);
    private readonly List<EmitProblem> _problems = [];

    // Entities keyed by object name, filled once in Emit() before any command is
    // generated, so Param() can recognize a nested single-id request record as "the id of an entity we already know
    // how to Get-/complete" and emit it as ZabbixReference<TEntity>[] instead of the raw nested-record array.
    private Dictionary<string, EntityInfo> _entityByObject = new(StringComparer.Ordinal);

    // The description's reviewed facts and the cmdlets' reviewed shape (schema/overlay.json's references, objects and
    // cmdlets sections, carried into schema/zabbix-api.json), as lookups.
    private readonly Dictionary<string, string> _idObjects = api.References.Where(r => r.Owner is null).ToDictionary(r => r.Wire, r => r.Object, StringComparer.Ordinal);
    private readonly Dictionary<(string Owner, string Wire), string> _ownedIdObjects = api.References.Where(r => r.Owner is not null).ToDictionary(r => (r.Owner!, r.Wire), r => r.Object);
    private readonly Dictionary<(string Object, string Wire), string> _requiredWhen = api.Services.SelectMany(s => s.RequiredWhen.Select(c => (s.Object, c.Wire, c.Text))).ToDictionary(c => (c.Object, c.Wire), c => "required when " + c.Text);
    private readonly Dictionary<string, CmdletObject> _cmdletObjects = api.CmdletShape.Objects.ToDictionary(o => o.Object, StringComparer.Ordinal);
    private readonly Dictionary<string, BuilderDescription> _builders = api.CmdletShape.Builders.ToDictionary(b => b.Concept, StringComparer.Ordinal);
    private Dictionary<string, HashSet<string>>? _requestOwners;

    // An id field's object: the one its owner's requests name it by (a template's groupid is a template group's), else the
    // one every other object's do (references in the description); null when the description names none. The owner of a
    // nested request record is every API object whose request reaches it.
    private string? EntityObjectFor(string recordName, string idWire)
    {
        HashSet<string>? owners = RequestOwners().TryGetValue(recordName, out HashSet<string>? found) ? found : null;
        string? owned = null;
        if (owners is { Count: > 0 } && owners.All(owner => _ownedIdObjects.TryGetValue((owner, idWire), out owned)) && owners.Select(owner => _ownedIdObjects[(owner, idWire)]).Distinct(StringComparer.Ordinal).Count() == 1)
            return owned;
        return _idObjects.TryGetValue(idWire, out string? general) ? general : null;
    }

    // Every record each API object's requests reach, through their object and list-of-object fields.
    private Dictionary<string, HashSet<string>> RequestOwners()
    {
        if (_requestOwners is not null) return _requestOwners;
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (TypeDescription request in api.Types.Where(t => t.Object is not null && t.Role == "request"))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>([request.Name]);
            while (pending.Count > 0)
            {
                string name = pending.Pop();
                if (!seen.Add(name) || !_types.TryGetValue(name, out TypeDescription? record)) continue;
                if (!owners.TryGetValue(name, out HashSet<string>? of)) owners[name] = of = new HashSet<string>(StringComparer.Ordinal);
                of.Add(request.Object!);
                foreach (FieldDescription field in record.Fields)
                {
                    string kind = field.Kind.StartsWith("list:", StringComparison.Ordinal) ? field.Kind.Substring(5) : field.Kind;
                    if (kind.StartsWith("object:", StringComparison.Ordinal)) pending.Push(kind.Substring(7));
                }
            }
        }
        return _requestOwners = owners;
    }

    private CmdletObject? CmdletsOf(string? apiObject) => apiObject is not null && _cmdletObjects.TryGetValue(apiObject, out CmdletObject? found) ? found : null;

    // An object left out on purpose (a hand-written command or a singleton covers it, cmdlets.objects.<object>.skip):
    // it raises no diagnostic; every other object is generated or reported.
    private bool IsSkipped(string apiObject) => CmdletsOf(apiObject)?.Skip is not null;

    // A secret is a name that ENDS in what it holds, in either spelling Zabbix uses (passwd: user.update, mediatype).
    // Anchored, so a name that only mentions one is not a secret: token_url is an OAuth URL, tokenid/tokens_status an id and a flag, and tls_psk_identity a public label the peer presents,
    // not the key — the hand-written Set-ZbxAutoregistration and Start-Zbx*Agent type it a plain string too.
    // ModuleShapeTests.SecretName is the same pattern for the reflective sweep.
    private static readonly Regex s_secretName = new(@"(password|passwd|passphrase|secret|apikey|psk|token)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The generated files (<c>ZabbixEntities.g.cs</c>, one file per entity's CRUD cmdlets, and the nested-record
    /// builders), and what could not be turned into a cmdlet or a builder.</summary>
    /// <param name="cancellationToken">Stops between files.</param>
    public EmitResult Emit(CancellationToken cancellationToken = default)
    {
        _problems.Clear();
        var files = new List<GeneratedSource>();
        List<EntityInfo> entities = [.. api.Services.Select(BuildEntity).Where(e => e is not null).Select(e => e!)];
        _entityByObject = entities.ToDictionary(e => e.Object, StringComparer.Ordinal);
        files.Add(new("ZabbixEntities.g.cs", EmitEntities(entities)));

        foreach (ServiceDescription service in api.Services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSkipped(service.Object) || !_entityByObject.TryGetValue(service.Object, out EntityInfo? entity)) continue;
            string? code = EmitCommands(service, entity);
            if (code is not null) files.Add(new(entity.Noun + "Commands.g.cs", code));
        }

        cancellationToken.ThrowIfCancellationRequested();
        files.Add(new("ZabbixNestedBuilders.g.cs", EmitNestedBuilders()));
        files.Add(new("ZabbixWireNames.g.cs", EmitWireNames()));
        return new([.. files], [.. _problems]);
    }

    // Every wire field name the description models anywhere, with the member name the records give it (the most common
    // one where records differ), so a field a read record does not model is shown by the name the others give it
    // (ZabbixAdditionalFieldsAdapter): a host's unmodelled templateid reads TemplateId, as a template's does.
    private string EmitWireNames()
    {
        var code = new StringBuilder(Header("The member name every modelled wire field has, for the fields a read record carries unmodelled."));
        code.Append("namespace Community.Zabbix.PowerShell;\n\n");
        code.Append("internal static partial class ZabbixWireNames\n{\n");
        code.Append("    private static partial global::System.Collections.Generic.Dictionary<string, string> Build() => new(global::System.StringComparer.Ordinal)\n    {\n");
        foreach (IGrouping<string, FieldDescription> wire in api.Types.SelectMany(t => t.Fields).GroupBy(f => f.Wire, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            string member = wire.GroupBy(f => f.Name, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
            code.Append("        [").Append(Lit(wire.Key)).Append("] = ").Append(Lit(member)).Append(",\n");
        }
        code.Append("    };\n}\n");
        return code.ToString();
    }

    // ----- entities ------------------------------------------------------------------------------------------

    // The entity's own top-level *ids option (property name, e.g. "TemplateIds") on its
    // *GetParameters, when it carries one — the field whose wire name is exactly the id field's wire plus "s" (the
    // same field EmitGet's idOptions loop already excludes from the reference-filter sweep for the identical reason:
    // "its plural already equals entity.IdField + 's' — -Id already covers it"). Some objects' .get rejects
    // filter.<idfield> outright (template.get: [] for a real id) but accepts this option; EmitEntities uses it,
    // when present, to build Get-Zbx<Entity> -Id's and New-/Set-'s post-write readback's selection instead of Filter.
    private sealed record EntityInfo(string Object, string Noun, TypeDescription Read, TypeDescription GetParameters, string IdField, string IdMember, string IdKind, string? NameField, string? NameMember, string? IdsMember);

    private EntityInfo? BuildEntity(ServiceDescription service)
    {
        if (IsSkipped(service.Object)) return null;
        TypeDescription? read = _byObjectRole[(service.Object, "read")].FirstOrDefault();
        TypeDescription? getParameters = _byObjectRole[(service.Object, "get")].FirstOrDefault();
        // ZabbixEntityShape.Create<TEntity, TParameters> requires TParameters : IZabbixGetParameters, which
        // ZabbixApiEmitter only implements when the record's own "getParameters" flag is set; a handful of get-list
        // methods (graphitem, hanode, httptest, task, trend) reuse a plain request shape that is not queryable that
        // way, and are left for a hand-written Get- (as they already partly are).
        if (read is null || getParameters is null || !getParameters.GetParameters) return null;

        FieldDescription? id = EntityIdField(service, read);
        if (id is null) return null;
        FieldDescription? nameField = EntityNameField(service.Object, read);
        FieldDescription? idsField = getParameters.Fields.FirstOrDefault(f => f.Kind == "list:id" && f.Wire == id.Wire + "s");
        return new EntityInfo(service.Object, Noun(service.Client), read, getParameters, id.Wire, id.Name, id.Kind, nameField?.Wire, nameField?.Name, idsField?.Name);
    }

    /// <summary>
    /// The read model's own id field: the description's <c>idField</c> when it names one (a host dashboard leads with its
    /// host's id and is itself the dashboard its <c>dashboardid</c> names). Some objects carry a legacy alias id first (Template's first <c>id</c>-kind field
    /// is <c>HostId</c>, not <c>TemplateId</c>: templates share the host table); <c>DeleteAsync</c>'s result names the
    /// field authoritatively (<c>ids:templateids</c>, the API's own plural of the record's own id wire), so it is tried
    /// first. Otherwise the record's leading field, which is the table's primary key in Zabbix's column order; a key the
    /// table keeps as a CUID (<c>auditid</c>, <c>ha_nodeid</c>) is a <c>string</c>, so the kind alone cannot pick it:
    /// picking "the first <c>id</c>-kind field" would make an audit record's user its key. Shared with
    /// <see cref="ZabbixTypesEmitter.GenerateFormat"/>, so a view leads with the same id the cmdlets select by.
    /// </summary>
    public static FieldDescription? EntityIdField(ServiceDescription service, TypeDescription read)
    {
        if (service is null) throw new ArgumentNullException(nameof(service));
        if (read is null) throw new ArgumentNullException(nameof(read));
        if (service.IdField is { } named && read.Fields.FirstOrDefault(f => f.Wire == named) is { } known) return known;
        MethodDescription? delete = service.Methods.FirstOrDefault(m => m.Name == "DeleteAsync");
        FieldDescription? id = null;
        if (delete is not null && delete.Result.StartsWith("ids:", StringComparison.Ordinal))
        {
            string plural = delete.Result[4..];
            if (plural.EndsWith("s", StringComparison.Ordinal))
                id = read.Fields.FirstOrDefault(f => f.Wire == plural[..^1]);
        }
        if (id is not null) return id;
        FieldDescription? leading = read.Fields.Count > 0 ? read.Fields[0] : null;
        return leading is { Kind: "id" } || (leading is { Kind: "string" } && leading.Wire.EndsWith("id", StringComparison.Ordinal)) ? leading : read.Fields.FirstOrDefault(f => f.Kind == "id");
    }

    /// <summary>
    /// The field a user names the object by: <c>host</c> (the technical, unique name of an object in the hosts table)
    /// before <c>name</c> (a display name that need not be unique; Host and Template carry both). <c>username</c> only names the user object itself:
    /// many unrelated records (an item's HTTP auth, a media type's SMTP account, a script's run-as account) also carry a
    /// <c>username</c> field. A user macro is named by its <c>macro</c> (<c>{$NAME}</c>), not its free-text
    /// <c>description</c>, as <c>Get-ZbxUserMacro -Name</c> takes it. Shared with <see cref="ZabbixTypesEmitter.GenerateFormat"/>.
    /// </summary>
    public static FieldDescription? EntityNameField(string apiObject, TypeDescription read)
    {
        if (read is null) throw new ArgumentNullException(nameof(read));
        FieldDescription? nameField = apiObject == "user" ? read.Fields.FirstOrDefault(f => f.Wire == "username") : null;
        // "host" is the technical name only of the objects in the hosts table (host, template, host prototype); a user
        // directory's "host" is its LDAP server, and userdirectory.get refuses a search on it ('Invalid parameter
        // "/search": unexpected parameter "host"').
        return nameField
            ?? (apiObject is "host" or "template" or "hostprototype" ? read.Fields.FirstOrDefault(f => f.Wire == "host") : null)
            ?? read.Fields.FirstOrDefault(f => f.Wire == "name")
            ?? read.Fields.FirstOrDefault(f => f.Wire == "macro")
            ?? read.Fields.FirstOrDefault(f => f.Wire == "description");
    }

    // The factory the generic Get- path builds the entity's *GetParameters through (ZabbixEntityShape.Materialize): the
    // common options the selection carries, plus every option the record requires, at its empty value; a required
    // option always has a mandatory parameter (EmitGetFilterParameter) whose value replaces it
    // before the request is sent.
    // The selection names its output's fields by the API's names, and the factory makes them the entity's fields.
    private static string Materialize(EntityInfo e, string parametersType)
        => "selection => new " + parametersType + " { Output = selection.Output is { } output ? [.. global::System.Linq.Enumerable.Select(output, static name => new global::Community.Zabbix.ZabbixField<global::Community.Zabbix." + Prefixed(e.Read.Name) + ">(name))] : null, Filter = new(selection.Filter), Search = new(selection.Search), SearchByAny = selection.SearchByAny, StartSearch = selection.StartSearch, SearchWildcardsEnabled = selection.SearchWildcardsEnabled, Limit = selection.Limit" + RequiredEmpties(e) + " }";

    // Every option the entity's *GetParameters requires, at its empty value, as object-initializer members (", HostIds = []"):
    // what every generated construction of the record must carry to compile; the mandatory parameter's value replaces it.
    private static string RequiredEmpties(EntityInfo e)
    {
        var code = new StringBuilder();
        foreach (FieldDescription field in e.GetParameters.Fields.Where(f => f.Required))
        {
            string empty = field.Kind.StartsWith("list:", StringComparison.Ordinal) ? "[]" : field.Kind == "string" ? "\"\"" : "default!";
            code.Append(", ").Append(field.Name).Append(" = ").Append(empty);
        }
        return code.ToString();
    }

    private static string EmitEntities(IReadOnlyList<EntityInfo> entities)
    {
        var code = new StringBuilder(Header("The generated half of <see cref=\"global::Community.Zabbix.PowerShell.ZabbixEntities\"/>: one shape per API object with a <c>read</c> model and a <c>get</c> parameters record."));
        code.Append("namespace Community.Zabbix.PowerShell;\n\ninternal static partial class ZabbixEntities\n{\n");
        code.Append("    private static partial global::System.Collections.Generic.Dictionary<global::System.Type, object> Build()\n    {\n");
        code.Append("        var shapes = new global::System.Collections.Generic.Dictionary<global::System.Type, object>();\n");
        foreach (EntityInfo e in entities)
        {
            string entityType = "global::Community.Zabbix." + Prefixed(e.Read.Name);
            string parametersType = "global::Community.Zabbix." + Prefixed(e.GetParameters.Name);
            string nameField = e.NameField ?? e.IdField;
            string nameMember = e.NameMember ?? e.IdMember;
            // The entity's own top-level *ids option (e.parametersType's IdsMember, e.g. TemplateIds) when it has
            // one, else filter.<idfield> (correct for entities like hostgroup whose .get accepts it).
            string idSelection = e.IdsMember is not null
                ? "ids => new " + parametersType + " { " + e.IdsMember + " = ids.Count == 0 ? null : ids" + RequiredEmpties(e) + " }"
                : "ids => new " + parametersType + " { Filter = ids.Count == 0 ? null : new() { [" + Lit(e.IdField) + "] = ids.ToArray() }" + RequiredEmpties(e) + " }";
            code.Append("        shapes[typeof(").Append(entityType).Append(")] = global::Community.Zabbix.PowerShell.ZabbixEntityShape.Create<").Append(entityType).Append(", ").Append(parametersType).Append(">(\n");
            code.Append("            ").Append(Lit(e.Object)).Append(", ").Append(Lit(e.IdField)).Append(", ").Append(Lit(nameField)).Append(",\n");
            // A CUID key is a string: the shape has no numeric id to read off a piped record (the command has no -Id set).
            string idOf = e.IdKind == "id" ? "entity => entity." + e.IdMember : "entity => (ulong?)null";
            code.Append("            ").Append(idOf).Append(", entity => entity.").Append(nameMember).Append(e.NameMember is null ? "?.ToString(global::System.Globalization.CultureInfo.InvariantCulture)" : string.Empty).Append(",\n");
            code.Append("            global::Community.Zabbix.PowerShell.ZabbixContracts.Of<").Append(parametersType).Append(">(), global::Community.Zabbix.PowerShell.ZabbixContracts.Of<global::System.Collections.Generic.IReadOnlyList<").Append(entityType).Append(">>(),\n");
            code.Append("            ").Append(idSelection).Append(",\n");
            code.Append("            ").Append(Materialize(e, parametersType)).Append(");\n");
        }
        code.Append("        return shapes;\n    }\n}\n");
        return code.ToString();
    }

    // ----- CRUD cmdlets ----------------------------------------------------------------------------------------

    private string? EmitCommands(ServiceDescription service, EntityInfo entity)
    {
        HashSet<string> methods = [.. service.Methods.Select(m => m.Name)];
        // ZabbixGetCmdlet<TEntity, TParameters> requires TParameters : IZabbixGetParameters, which ZabbixApiEmitter
        // only implements on a "get" record whose own "getParameters" flag is set (the description's own signal that
        // the record is queryable, not just a request shape reused for a get-object call).
        bool emitGet = methods.Contains("GetAsync") && CmdletsOf(service.Object)?.HandWrittenGet is null && entity.GetParameters.GetParameters;
        bool emitNew = methods.Contains("CreateAsync");
        bool emitSet = methods.Contains("UpdateAsync");
        bool emitRemove = methods.Contains("DeleteAsync");
        if (!emitGet && !emitNew && !emitSet && !emitRemove) return null;

        var code = new StringBuilder(Header("Generated <c>Get</c>/<c>New</c>/<c>Set</c>/<c>Remove</c> for <c>" + service.Object + "</c>."));
        code.Append("namespace Community.Zabbix.PowerShell.Commands.Generated;\n\n");
        code.Append("using global::Community.Zabbix.PowerShell;\n\n");

        if (emitGet) EmitGet(code, service, entity);
        if (emitNew) EmitNew(code, service, entity);
        if (emitSet) EmitSet(code, service, entity);
        if (emitRemove) EmitRemove(code, service, entity);
        return code.ToString();
    }

    // What a generated Get- is made of, read once from the entity's .get parameters: the record types, the options that
    // select by another object (the help partial's GetFilters), the -Select* options, the get switches, the time window
    // and the value type.
    private sealed record GetShape(
        ServiceDescription Service,
        EntityInfo Entity,
        string EntityType,
        string ParametersType,
        List<(FieldDescription Field, string Concept, EntityInfo? Target)> Filters,
        List<FieldDescription> Selects,
        List<(GetSwitch Switch, FieldDescription Field)> Switches,
        List<(GetList List, FieldDescription Field)> Lists,
        FieldDescription? From,
        FieldDescription? Till,
        FieldDescription? ValueType);

    private GetShape GetShapeOf(ServiceDescription service, EntityInfo entity)
    {
        IReadOnlyList<FieldDescription> fields = entity.GetParameters.Fields;
        return new(
            service,
            entity,
            "global::Community.Zabbix." + Prefixed(entity.Read.Name),
            "global::Community.Zabbix." + Prefixed(entity.GetParameters.Name),
            GetFilters(entity),
            // Every selectX option (an output kind, wire "select...").
            [.. fields.Where(f => f.Kind.StartsWith(ZabbixTypedFields.Output, StringComparison.Ordinal) && f.Wire != "output")],
            // The get options taken as switches (cmdlets.getSwitches: -Editable, usermacro.get's -Global, problem.get's
            // -Recent), each where the entity's .get parameters carry it: not every .get has them, so they are not among
            // the common options IZabbixGetParameters carries.
            [.. api.CmdletShape.GetSwitches
                .Select(option => (option, fields.FirstOrDefault(f => f.Wire == option.Wire && f.Kind == "apibool")))
                .Where(option => option.Item2 is not null)
                .Select(option => (option.option, option.Item2!))],
            // The get options taken as enum arrays (cmdlets.getLists: problem.get's and event.get's -Severity), each
            // where the entity's .get parameters carry it as a list of enum values.
            [.. api.CmdletShape.GetLists
                .Select(option => (option, fields.FirstOrDefault(f => f.Wire == option.Wire && f.Kind.StartsWith("list:enum:", StringComparison.Ordinal))))
                .Where(option => option.Item2 is not null)
                .Select(option => (option.option, option.Item2!))],
            fields.FirstOrDefault(f => f.Wire == "time_from" && f.Kind == "clock"),
            fields.FirstOrDefault(f => f.Wire == "time_till" && f.Kind == "clock"),
            fields.FirstOrDefault(f => f.Wire == "history" && f.Kind.StartsWith("enum:", StringComparison.Ordinal)));
    }

    // Get-Zbx<Entity> on ZabbixGetCmdlet<TEntity, TParameters>: its help, then each part of the class in order.
    private void EmitGet(StringBuilder code, ServiceDescription service, EntityInfo entity)
    {
        GetShape get = GetShapeOf(service, entity);
        EmitGetHelp(code, service, entity, get.Filters, hasSelect: get.Selects.Count > 0);
        code.Append("[global::System.Management.Automation.Cmdlet(global::System.Management.Automation.VerbsCommon.Get, \"Zbx").Append(entity.Noun).Append("\", DefaultParameterSetName = ZabbixParameterSets.All, SupportsPaging = true)]\n");
        code.Append("[global::System.Management.Automation.OutputType(typeof(").Append(get.EntityType).Append("))]\n");
        code.Append("public sealed class Get").Append(entity.Noun).Append("Command : ZabbixGetCmdlet<").Append(get.EntityType).Append(", ").Append(get.ParametersType).Append(">\n{\n");
        EmitGetSelection(code, get);
        // Every *ids option the .get parameters carry becomes a filter parameter here, except the entity's own id (its
        // plural already equals entity.IdField + "s" — -Id already covers it). One that names a known entity becomes a
        // ZabbixReference<TEntity>[] (ids, names, or objects, resolved in one .get, same as New-/Set-'s nested reference
        // fields); one that does not (an option like alert.objectids whose target type varies with another field) is
        // left as a plain raw-id string[]? so the option is still reachable, just without name resolution.
        foreach ((FieldDescription field, string concept, EntityInfo? target) in get.Filters)
            EmitGetFilterParameter(code, field, concept, target, field.Required);
        EmitGetSwitches(code, get);
        EmitGetWindow(code, get);
        EmitApplyEntityFilters(code, get);
        EmitGetQuery(code, get);
        EmitApplyReferenceFilters(code, get);
        code.Append("}\n\n");
    }

    // -Name, -Limit, -Id and -Filter: the four ways the base selects.
    private void EmitGetSelection(StringBuilder code, GetShape get)
    {
        EntityInfo entity = get.Entity;
        if (entity.NameField is not null)
        {
            code.Append("    /// <summary>The objects' names, case-insensitive; <c>*</c> and <c>?</c> are wildcards, brackets are literal. A piped name selects the same way.</summary>\n");
            code.Append("    [global::System.Management.Automation.Parameter(Mandatory = true, Position = 0, ParameterSetName = ZabbixParameterSets.ByName)]\n");
            code.Append("    [global::System.Management.Automation.SupportsWildcards]\n");
            // The name in the form the shell writes it, as New-/Set- take the same field (a user macro's backup_dir is
            // {$BACKUP_DIR}).
            code.Append(ValueFormAttributes(CmdletsOf(entity.Object)?.ValueForms, entity.NameField));
            code.Append("    [ZabbixCompleter<").Append(get.EntityType).Append(">]\n");
            code.Append("    public override string[]? Name { get; set; }\n\n");
        }
        else
        {
            code.Append("    /// <summary>Not used: a ").Append(Prose(entity.Noun)).Append(" has no name field; use -Id or -Filter.</summary>\n");
            code.Append("    public override string[]? Name { get; set; }\n\n");
        }
        // -Limit in the sets that select many: All, and ByName where there is a name.
        code.Append("    /// <summary>At most this many objects.</summary>\n");
        code.Append("    [global::System.Management.Automation.Parameter(ParameterSetName = ZabbixParameterSets.All)]\n");
        if (entity.NameField is not null) code.Append("    [global::System.Management.Automation.Parameter(ParameterSetName = ZabbixParameterSets.ByName)]\n");
        code.Append("    [global::System.Management.Automation.ValidateRange(1, int.MaxValue)]\n");
        code.Append("    public override int? Limit { get; set; }\n\n");

        // -SortField completes the fields the .get sorts by (its sortfield rule, else the service's $sortColumns, carried
        // into the description as the get record's "sortable"), not file names.
        code.Append("    /// <summary>Sort by these fields, in order (<c>sortfield</c>); Tab completes the fields ").Append(entity.Object).Append(".get sorts by.</summary>\n");
        code.Append("    [global::System.Management.Automation.Parameter]\n");
        if (entity.GetParameters.Sortable.Length > 0)
            code.Append("    [global::System.Management.Automation.ArgumentCompletions(").Append(string.Join(", ", entity.GetParameters.Sortable.OrderBy(f => f, StringComparer.Ordinal).Select(Lit))).Append(")]\n");
        code.Append("    public override string[]? SortField { get; set; }\n\n");

        // -Id: abstract on the base so each entity names its own id member as the alias: piped objects carrying the id
        // (HostId, NetworkDiscoveryRuleId...) bind by property name.
        // A piped id or name is the base's InputObject, not a by-value binding here: with conversion the binder would bind
        // any piped record to a by-value parameter as its text before trying its properties.
        if (entity.IdKind != "id")
        {
            // A CUID key (a 25-character string) is not a ulong: the object has no -Id set. Its own ids go through -Filter.
            code.Append("    /// <summary>Not a parameter: a ").Append(Prose(entity.Noun)).Append("'s id is a CUID string, not a number; select by the other parameters or by <c>-Filter</c>.</summary>\n");
            code.Append("    public override ulong[]? Id { get; set; }\n\n");
        }
        else
        {
            code.Append("    /// <summary>The objects' ids; piped objects carrying <c>").Append(entity.IdMember).Append("</c> bind here, and a piped id selects the same way.</summary>\n");
            code.Append("    [global::System.Management.Automation.Parameter(Mandatory = true, ParameterSetName = ZabbixParameterSets.ById, ValueFromPipelineByPropertyName = true)]\n");
            // Also by property name in the default set: PowerShell binds a piped record in the default set first, so without
            // the id bound there a reference filter takes the record's HostId and the record's own id is never bound (Get-ZbxItem -Id 23 |
            // Get-ZbxItem would read every item of the host; Get-ZbxItem | Get-ZbxHistory every item's history). Bound
            // there, the id selects, the filter narrows (ZabbixGetCmdlet.Selection).
            code.Append("    [global::System.Management.Automation.Parameter(ParameterSetName = ZabbixParameterSets.All, ValueFromPipelineByPropertyName = true)]\n");
            if (entity.IdMember != "Id") code.Append("    [global::System.Management.Automation.Alias(").Append(Lit(entity.IdMember)).Append(")]\n");
            code.Append("    [global::Community.Zabbix.PowerShell.ZabbixId]\n    public override ulong[]? Id { get; set; }\n\n");
        }

        // -Filter is abstract on the base for the same "an attribute cannot take a type parameter" reason -Name is
        // (ZabbixGetCmdlet.cs). No attribute is needed to wire-remap a bare -Filter @{ hostid = '1' } hashtable:
        // ZabbixRecordConverter is registered as parametersType's own <TypeConverter> in the generated
        // Community.Zabbix.types.ps1xml, so PowerShell's own parameter binder does it.
        code.Append("    /// <summary>The full selection, as ").Append(get.Service.Object).Append(".get's own parameters (a typed record; a hashtable with the API's field names casts to it).</summary>\n");
        code.Append("    [global::System.Management.Automation.Parameter(Mandatory = true, ParameterSetName = ZabbixParameterSets.ByFilter)]\n");
        code.Append("    public override ").Append(get.ParametersType).Append("? Filter { get; set; }\n\n");
    }

    // -SelectX, one SwitchParameter per select option, not a "switch or field list" type: scripts use Select* bare, and
    // PowerShell's parameter binder only lets a bare, argument-less "-Name" bind to a parameter whose declared type is
    // exactly SwitchParameter (any other type, even one with an implicit conversion from SwitchParameter, requires a
    // following value: "Missing an argument for parameter 'SelectHosts'"). The field-list form
    // (-Filter @{ SelectHosts = @('hostid','host') }) is still reachable through the typed -Filter object. Then the get
    // switches.
    private static void EmitGetSwitches(StringBuilder code, GetShape get)
    {
        foreach (FieldDescription field in get.Selects)
        {
            code.Append("    /// <summary>").Append(Doc(field.Summary, "Returns every field of the related object(s). Wire field " + field.Wire + "."))
                .Append("</summary>\n    [global::System.Management.Automation.Parameter]\n    public global::System.Management.Automation.SwitchParameter ").Append(field.Name).Append(" { get; set; }\n\n");
        }
        foreach ((GetSwitch option, _) in get.Switches)
        {
            code.Append("    /// <summary>").Append(Doc(option.Summary + " Wire field " + option.Wire + ".", option.Wire)).Append("</summary>\n");
            code.Append("    [global::System.Management.Automation.Parameter]\n    public global::System.Management.Automation.SwitchParameter ").Append(option.Parameter).Append(" { get; set; }\n\n");
        }
        foreach ((GetList option, FieldDescription field) in get.Lists)
        {
            code.Append("    /// <summary>").Append(Doc(option.Summary + " Wire field " + option.Wire + ".", option.Wire)).Append("</summary>\n");
            code.Append("    [global::System.Management.Automation.Parameter]\n    public global::Community.Zabbix.").Append(Prefixed(field.Kind["list:enum:".Length..])).Append("[]? ").Append(option.Parameter).Append(" { get; set; }\n\n");
        }
    }

    // -From/-Till: a .get that takes a time window (time_from/time_till: history, problem, event, alert, auditlog) takes
    // it as Get-ZbxTrend does, a clock, rather than only through -Filter. -ValueType: history.get reads one value type per
    // call (history, default unsigned), so a text or float item's values would come back empty; bound by property name,
    // a piped item's own ValueType picks its table.
    private static void EmitGetWindow(StringBuilder code, GetShape get)
    {
        if (get.From is not null)
        {
            code.Append("    /// <summary>The window's start (a date, Unix seconds, or a clock). Wire field time_from.</summary>\n");
            code.Append("    [global::System.Management.Automation.Parameter]\n    [global::Community.Zabbix.PowerShell.ZabbixClock]\n    public global::System.DateTimeOffset? From { get; set; }\n\n");
        }
        if (get.Till is not null)
        {
            code.Append("    /// <summary>The window's end (a date, Unix seconds, or a clock). Wire field time_till.</summary>\n");
            code.Append("    [global::System.Management.Automation.Parameter]\n    [global::Community.Zabbix.PowerShell.ZabbixClock]\n    public global::System.DateTimeOffset? Till { get; set; }\n\n");
        }
        if (get.ValueType is not null)
        {
            code.Append("    /// <summary>The value type to read (the history table); a piped item's own <c>ValueType</c> binds here. Wire field history.</summary>\n");
            code.Append("    [global::System.Management.Automation.Parameter(ValueFromPipelineByPropertyName = true)]\n");
            code.Append("    public global::Community.Zabbix.").Append(Prefixed(get.ValueType.Kind["enum:".Length..])).Append("? ValueType { get; set; }\n\n");
        }
    }

    // ApplyEntityFilters always overrides (not just when the entity has its own -Select*/-Editable): folding
    // the sort (-SortField and -SortOrder)/Search/SearchByAny/StartSearch/ExcludeSearch/SearchWildcardsEnabled onto an
    // existing parameters instance needs `with`, which only compiles against the concrete record type this generated
    // class knows (TParameters is generic in the base — see ZabbixGetCmdlet's doc comment on ApplyEntityFilters).
    // SortWire()/SearchDictionary() are the base's plain helpers.
    private void EmitApplyEntityFilters(StringBuilder code, GetShape get)
    {
        string parametersType = get.ParametersType;
        code.Append("    /// <inheritdoc/>\n    protected override ").Append(parametersType).Append(" ApplyEntityFilters(").Append(parametersType).Append(" parameters)\n    {\n");
        code.Append("        if (SortWire(parameters.Sort) is { } sort) parameters = parameters with { Sort = sort };\n");
        code.Append("        if (SearchDictionary() is { } search) parameters = parameters with { Search = new(search) };\n");
        code.Append("        if (SearchByAny.IsPresent) parameters = parameters with { SearchByAny = true };\n");
        code.Append("        if (StartSearch.IsPresent) parameters = parameters with { StartSearch = true };\n");
        code.Append("        if (ExcludeSearch.IsPresent) parameters = parameters with { ExcludeSearch = true };\n");
        code.Append("        if (SearchWildcardsEnabled.IsPresent) parameters = parameters with { SearchWildcardsEnabled = true };\n");
        foreach (FieldDescription field in get.Selects)
            Obsolete(code, field).Append("        if (").Append(field.Name).Append(".IsPresent) parameters = parameters with { ").Append(field.Name).Append(" = global::Community.Zabbix.ZabbixOutput.Extend };\n").Append(Restored(field));
        foreach ((GetSwitch option, FieldDescription field) in get.Switches)
            code.Append("        if (").Append(option.Parameter).Append(".IsPresent) parameters = parameters with { ").Append(field.Name).Append(" = true };\n");
        foreach ((GetList option, FieldDescription field) in get.Lists)
            Obsolete(code, field).Append("        if (").Append(option.Parameter).Append(" is { Length: > 0 }) parameters = parameters with { ").Append(field.Name).Append(" = ").Append(option.Parameter).Append(" };\n").Append(Restored(field));
        if (get.From is not null)
            code.Append("        if (From is { } from) parameters = parameters with { ").Append(get.From.Name).Append(" = from };\n");
        if (get.Till is not null)
            code.Append("        if (Till is { } till) parameters = parameters with { ").Append(get.Till.Name).Append(" = till };\n");
        if (get.ValueType is not null)
            code.Append("        if (ValueType is { } valueType) parameters = parameters with { ").Append(get.ValueType.Name).Append(" = valueType };\n");
        if (LimitsById(get.Service.Object, get.Entity))
            code.Append("        if (Id is { Length: > 0 } && Limit is { } limit) parameters = parameters with { Limit = limit };\n");
        code.Append("        return parameters;\n    }\n\n");
    }

    // Query: the count when -CountOutput asks for it, else every page of the objects.
    private static void EmitGetQuery(StringBuilder code, GetShape get)
    {
        string parametersType = get.ParametersType;
        ServiceDescription service = get.Service;
        code.Append("    /// <inheritdoc/>\n");
        code.Append("    protected override void Query(ZabbixClient client, ").Append(parametersType).Append(" parameters)\n    {\n");
        code.Append("        if (CountOutput.IsPresent) { if (TryRun(cancellationToken => client.").Append(service.Property)
            .Append(".CountAsync(parameters, cancellationToken), out long count, parameters)) WriteObject(count); return; }\n");
        code.Append("        WriteAll(cancellationToken => client.").Append(service.Property).Append(".GetAsync(parameters, cancellationToken), parameters);\n    }\n\n");
        code.Append("    /// <inheritdoc/>\n");
        code.Append("    protected override global::System.Threading.Tasks.Task<long> CountAsync(ZabbixClient client, ").Append(parametersType)
            .Append(" parameters, global::System.Threading.CancellationToken cancellationToken)\n    {\n        global::System.ArgumentNullException.ThrowIfNull(client);\n        return client.")
            .Append(service.Property).Append(".CountAsync(parameters, cancellationToken);\n    }\n");
    }

    // The reference filters, resolved to ids in one .get each before the selection is sent.
    private static void EmitApplyReferenceFilters(StringBuilder code, GetShape get)
    {
        string parametersType = get.ParametersType;
        List<(FieldDescription Field, string Concept, EntityInfo? Target)> idOptions = get.Filters;
        if (idOptions.Count == 0) return;
        code.Append("\n    /// <inheritdoc/>\n    protected override bool HasReferenceFilters => ")
            .Append(string.Join(" || ", idOptions.Select(o => (o.Target is not null ? FilterProperty(o.Concept) : Singular(o.Field)) + " is not null"))).Append(";\n\n");
        code.Append("    /// <inheritdoc/>\n    protected override async global::System.Threading.Tasks.Task<").Append(parametersType)
            .Append("> ApplyReferenceFiltersAsync(").Append(parametersType).Append(" parameters, global::System.Threading.CancellationToken cancellationToken)\n    {\n");
        foreach ((FieldDescription field, string concept, EntityInfo? target) in idOptions)
        {
            if (target is not null)
            {
                string referenceEntityType = "global::Community.Zabbix." + Prefixed(target.Read.Name);
                code.Append("        if (").Append(FilterProperty(concept)).Append(" is not null) parameters = parameters with { ").Append(field.Name)
                    .Append(" = await ResolveAsync<").Append(referenceEntityType).Append(">(").Append(FilterProperty(concept))
                    .Append(", cancellationToken).ConfigureAwait(false) };\n");
            }
            else
            {
                code.Append("        if (").Append(Singular(field)).Append(" is not null) parameters = parameters with { ").Append(field.Name).Append(" = ").Append(Singular(field)).Append(" };\n");
            }
        }
        code.Append("        return parameters;\n    }\n");
    }

    // The entity a Get- filter selects by: the object its option's id refers to (the option's wire less its "s": hostids
    // is a hostid, references in the description, a template's groupids its template groups), else the entity the
    // concept spells but for case (Graph -> graph, MediaType -> mediatype); null when neither names one (alert.get's
    // objectids, whose object varies with another field), and the filter takes raw ids. The concept is the option's name
    // less "Ids" (GetFilters).
    private EntityInfo? TryGetFilterEntity(string ownerObject, string concept)
    {
        FieldDescription? option = _entityByObject.TryGetValue(ownerObject, out EntityInfo? owner)
            ? owner.GetParameters.Fields.FirstOrDefault(f => f.Kind == "list:id" && (f.Name == concept + "Ids" || f.Name == concept))
            : null;
        if (option is not null)
        {
            string id = option.Wire.EndsWith("s", StringComparison.Ordinal) ? option.Wire.Substring(0, option.Wire.Length - 1) : option.Wire;
            if (_ownedIdObjects.TryGetValue((ownerObject, id), out string? owned) || _idObjects.TryGetValue(id, out owned))
                return _entityByObject.GetValueOrDefault(owned);
        }
        // A case-insensitive key scan rather than lower-casing the concept (CA1308: a culture-sensitive transform is the
        // wrong tool for an ASCII object-name comparison; the entity table is small).
        foreach (KeyValuePair<string, EntityInfo> candidate in _entityByObject)
        {
            if (string.Equals(candidate.Key, concept, StringComparison.OrdinalIgnoreCase)) return candidate.Value;
        }
        return null;
    }

    private static void EmitGetFilterParameter(StringBuilder code, FieldDescription field, string concept, EntityInfo? target, bool required)
    {
        if (target is not null)
        {
            string entityType = "global::Community.Zabbix." + Prefixed(target.Read.Name);
            code.Append("    /// <summary>Filters by ").Append(target.Object).Append(": ids, names, or objects (e.g. from Get-Zbx").Append(target.Noun)
                .Append(").").Append(required ? " Required." : string.Empty)
                .Append(" From the pipeline it takes the ").Append(concept).Append("Id of a piped ").Append(target.Object)
                .Append(" or of an object that is not a Zabbix record; another Zabbix record's ").Append(concept).Append("Id is ignored.")
                .Append(" Wire field ").Append(field.Wire).Append(".</summary>\n");
            // What the help says binds is what ZabbixGetCmdlet.DropForeignReferences keeps: the binder matches the property
            // name alone, and the command unbinds a reference another Zabbix record carried.
            // Piped from Get-Zbx<Target> (e.g. Get-ZbxHost | Get-ZbxItem) binds by property name, not by position/value: the
            // <Concept>Id alias below is the property the piped object itself carries (Get-ZbxHost's own HostId), and
            // ValueFromPipelineByPropertyName has to be present too or PowerShell never even looks (an alias alone only
            // helps a named argument).
            // Mandatory in every set, -Filter's included: one [Parameter] in no set is what PowerShell's binder defers to
            // the pipeline (per-set Mandatory makes a piped Get-ZbxHost | Get-ZbxHostDashboard fail on the ById set).
            code.Append(required ? "    [global::System.Management.Automation.Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)]\n" : "    [global::System.Management.Automation.Parameter(ValueFromPipelineByPropertyName = true)]\n");
            List<string> aliases = [concept + "Id"];
            // A concept that would hide a PSCmdlet member (-Host) is the property <Concept>Reference and keeps its spelling.
            string property = FilterProperty(concept);
            if (property != concept) aliases.Insert(0, concept);
            aliases.RemoveAll(alias => string.Equals(alias, property, StringComparison.OrdinalIgnoreCase));
            code.Append("    [global::System.Management.Automation.Alias(").Append(string.Join(", ", aliases.Distinct(StringComparer.OrdinalIgnoreCase).Select(Lit))).Append(")]\n");
            code.Append("    [ZabbixReference<").Append(entityType).Append(">]\n");
            code.Append("    [ZabbixCompleter<").Append(entityType).Append(">]\n");
            code.Append("    public global::Community.Zabbix.PowerShell.ZabbixReference<").Append(entityType).Append(">[]? ").Append(property).Append(" { get; set; }\n\n");
        }
        else
        {
            code.Append("    /// <summary>Filters by raw ").Append(field.Wire)
                .Append(" (ids only: the object type these ids refer to varies, so names cannot be resolved). Wire field ")
                .Append(field.Wire).Append(".</summary>\n");
            code.Append(required ? "    [global::System.Management.Automation.Parameter(Mandatory = true)]\n" : "    [global::System.Management.Automation.Parameter]\n");
            code.Append("    public ulong[]? ").Append(Singular(field)).Append(" { get; set; }\n\n");
        }
    }

    // New-ZbxHost's single-interface shorthand: -InterfaceType/-InterfaceIp/-InterfaceDns/
    // -InterfacePort/-UseIp build exactly one interface, the most common way scripts create a host in one line, which
    // -Interface (a typed ZabbixHostInterfaceRequest[], the "list:object" shape Param() emits) cannot express. The
    // shorthand is additive, not exclusive: passing both -Interface and the shorthand appends the shorthand-built
    // interface to whatever -Interface already carries. The object and its list field are the description's
    // cmdlets.objects.<object>.interfaceShorthand.

    private void EmitNew(StringBuilder code, ServiceDescription service, EntityInfo entity)
    {
        TypeDescription? request = _byObjectRole[(service.Object, "request")].FirstOrDefault(t => t.Method == service.Object + ".create");
        if (request is null) { _problems.Add(new(ZabbixDiagnostics.NotGenerated, "create is a method of " + service.Object + " but no *CreateRequest is described", service.Object, null)); return; }
        string entityType = "global::Community.Zabbix." + Prefixed(entity.Read.Name);
        string requestType = "global::Community.Zabbix." + Prefixed(request.Name);
        List<(FieldDescription Field, ParamInfo Info)> parameters = Superseding([.. request.Fields.Select(f => (f, Param(f, request.Name))).Where(p => p.Item2 is not null).Select(p => (p.f, p.Item2!))]);
        HashSet<string> siblingNames = [.. parameters.Select(p => p.Field.Name)];
        FieldDescription? shorthandField = CmdletsOf(service.Object)?.InterfaceShorthand is { } shorthand ? parameters.Select(p => p.Field).FirstOrDefault(f => f.Wire == shorthand) : null;
        List<(FieldDescription Field, ParamInfo Info)> escapableRequired = [.. parameters.Where(p => IsEscapableRequired(service.Object, p.Field))];

        EmitNewHelp(code, service, entity, parameters, siblingNames);
        code.Append("[global::System.Management.Automation.Cmdlet(global::System.Management.Automation.VerbsCommon.New, \"Zbx").Append(entity.Noun).Append("\", SupportsShouldProcess = true, ConfirmImpact = global::System.Management.Automation.ConfirmImpact.Medium)]\n");
        code.Append("[global::System.Management.Automation.OutputType(typeof(").Append(entityType).Append("))]\n");
        // Partial: a hand-written part may add to a generated New- (Commands/Generated/NewTokenCommand.Generate.cs).
        code.Append("public sealed partial class New").Append(entity.Noun).Append("Command : ZabbixNewCmdlet<").Append(entityType).Append(">\n{\n");
        Dictionary<FieldDescription, int> positions = CreatePositions(entity, parameters);
        foreach ((FieldDescription field, ParamInfo info) in parameters)
            EmitParameter(code, service.Object, field, info, siblingNames, create: true, position: positions.TryGetValue(field, out int at) ? at : null);
        EmitPropertiesParameter(code);
        if (shorthandField is not null) EmitHostInterfaceShorthandParameters(code);
        bool hasReference = parameters.Any(p => p.Info.Reference is not null);
        if (hasReference)
        {
            code.Append("    /// <inheritdoc/>\n    protected override async global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<ulong>> CreateAsync(global::System.Threading.CancellationToken cancellationToken)\n    {\n");
            EmitRequiredFieldValidation(code, "        ", service.Object, requestType, escapableRequired);
            code.Append("        var request = new ").Append(requestType).Append("\n        {\n");
            foreach ((FieldDescription field, ParamInfo info) in parameters) Obsolete(code, field).Append("            ").Append(field.Name).Append(" = ").Append(AssignWithShorthand(service.Object, field, info, shorthandField)).Append(",\n").Append(Restored(field));
            code.Append("        };\n");
            code.Append("        return [await ZabbixClient.").Append(service.Property).Append(".CreateAsync(global::Community.Zabbix.PowerShell.ZabbixPropertyOverrides.Apply(request, AdditionalProperty), cancellationToken).ConfigureAwait(false)];\n    }\n");
        }
        else
        {
            code.Append("    /// <inheritdoc/>\n    protected override async global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<ulong>> CreateAsync(global::System.Threading.CancellationToken cancellationToken)\n    {\n");
            EmitRequiredFieldValidation(code, "        ", service.Object, requestType, escapableRequired);
            code.Append("        return [await ZabbixClient.").Append(service.Property).Append(".CreateAsync(global::Community.Zabbix.PowerShell.ZabbixPropertyOverrides.Apply(new ").Append(requestType).Append("\n        {\n");
            foreach ((FieldDescription field, ParamInfo info) in parameters) Obsolete(code, field).Append("            ").Append(field.Name).Append(" = ").Append(AssignWithShorthand(service.Object, field, info, shorthandField)).Append(",\n").Append(Restored(field));
            code.Append("        }, AdditionalProperty), cancellationToken).ConfigureAwait(false)];\n    }\n");
        }
        if (shorthandField is not null) EmitHostInterfaceShorthandMerge(code);
        // -WhatIf and the prompt name the object to create (host 'web-01'), so a piped row shows what it binds.
        if (parameters.Select(p => p.Field).FirstOrDefault(f => f.Wire == entity.NameField && f.Kind == "string") is { } nameField)
        {
            code.Append("\n    /// <inheritdoc/>\n    protected override string? CreatedName => ").Append(CmdletPropertyName(service.Object, nameField))
                .Append(" ?? AdditionalProperty?[").Append(Lit(nameField.Wire)).Append("]?.ToString();\n");
        }
        code.Append("}\n\n");
    }

    // New-Zbx* takes the object's name first and its next required scalar second
    // (New-ZbxHostGroup Databases, New-ZbxTrigger 'CPU high' 'last(/h/k)>90', New-ZbxAction a Triggers). A rule over
    // the create request: position 0 is the entity's own name field, position
    // 1 the first other required field that is a plain value (not an id, a reference, a list or a record).
    private Dictionary<FieldDescription, int> CreatePositions(EntityInfo entity, List<(FieldDescription Field, ParamInfo Info)> parameters)
    {
        var positions = new Dictionary<FieldDescription, int>();
        FieldDescription? name = parameters.Select(p => p.Field).FirstOrDefault(f => f.Wire == entity.NameField && f.Kind == "string");
        if (name is null)
        {
            // An object with no name of its own (a host interface) is created on its host: that comes first.
            if (parameters.FirstOrDefault(p => p.Field.Wire == "hostid" && p.Info.IsId && p.Info.Reference is null) is { Field: { } owner }) positions[owner] = 0;
            return positions;
        }
        positions[name] = 0;
        FieldDescription? second = parameters
            .Where(p => p.Field != name && p.Field.Required && p.Info.Reference is null && !p.Info.IsId && !p.Info.Secret)
            .Select(p => p.Field)
            .FirstOrDefault(f => f.Kind is "string" or "int" or "long" or "double" || f.Kind.StartsWith("enum:", StringComparison.Ordinal));
        if (second is not null && !_requiredWhen.ContainsKey((entity.Object, second.Wire))) positions[second] = 1;
        return positions;
    }

    // A raw-id list the API removed in a later version (maintenance hostids/groupids and
    // usergroup userids, removed in 7.4 — ZabbixApiEmitter's own [ZabbixUnsupported], refused before sending) beside
    // the reference list that replaced it (hosts/groups/users: [{hostid}], accepted by every version). The removed
    // field gets no parameter of its own: the reference parameter (-Host) takes ids too, and sends hosts: [{hostid}]
    // on every version.
    private static List<(FieldDescription Field, ParamInfo Info)> Superseding(List<(FieldDescription Field, ParamInfo Info)> parameters)
    {
        HashSet<string> replacedBy = [.. parameters.Where(p => p.Info.Reference is not null).Select(p => ReferenceAlias(p.Field.Name))];
        return [.. parameters.Where(p => !(p.Field.Kind == "list:id" && p.Field.Removed is not null && replacedBy.Contains(p.Field.Name)))];
    }

    private string AssignWithShorthand(string? serviceObject, FieldDescription field, ParamInfo info, FieldDescription? shorthandField)
    {
        string cmdletProperty = CmdletPropertyName(serviceObject, field);
        return field == shorthandField
            ? "MergeInterfaces(" + Assign(serviceObject, field, info, cmdletProperty) + ")"
            : Assign(serviceObject, field, info, cmdletProperty);
    }

    private static void EmitHostInterfaceShorthandParameters(StringBuilder code)
    {
        code.Append("    /// <summary>The type of the one interface this shorthand adds to -Interface. Default\n");
        code.Append("    /// Agent when any of -InterfaceIp/-InterfaceDns/-InterfacePort/-UseIp is bound but -InterfaceType is not.</summary>\n");
        code.Append("    [global::System.Management.Automation.Parameter(ValueFromPipelineByPropertyName = true)]\n    public global::Community.Zabbix.ZabbixInterfaceType? InterfaceType { get; set; }\n\n");
        code.Append("    /// <summary>The shorthand interface's IP address. Default 127.0.0.1.</summary>\n");
        code.Append("    [global::System.Management.Automation.Parameter(ValueFromPipelineByPropertyName = true)]\n    public string? InterfaceIp { get; set; }\n\n");
        code.Append("    /// <summary>The shorthand interface's DNS name.</summary>\n");
        code.Append("    [global::System.Management.Automation.Parameter(ValueFromPipelineByPropertyName = true)]\n    public string? InterfaceDns { get; set; }\n\n");
        code.Append("    /// <summary>The shorthand interface's port. Default 10050 (161 for -InterfaceType Snmp).</summary>\n");
        code.Append("    [global::System.Management.Automation.Parameter(ValueFromPipelineByPropertyName = true)]\n    public string? InterfacePort { get; set; }\n\n");
        code.Append("    /// <summary>Connect by IP instead of DNS for the shorthand interface. Default: by IP, or by DNS when only -InterfaceDns is given.</summary>\n");
        code.Append("    [global::System.Management.Automation.Parameter(ValueFromPipelineByPropertyName = true)]\n    public global::System.Management.Automation.SwitchParameter UseIp { get; set; } = new global::System.Management.Automation.SwitchParameter(true);\n\n");
    }

    private static void EmitHostInterfaceShorthandMerge(StringBuilder code)
    {
        code.Append('\n');
        code.Append("    // New-ZbxHost's single-interface shorthand: builds exactly one\n");
        code.Append("    // ZabbixHostInterfaceRequest from the shorthand parameters above and appends it to -Interface. Gated on\n");
        code.Append("    // MyInvocation.BoundParameters, not a null check: every shorthand parameter has a non-null default (UseIp\n");
        code.Append("    // defaults true), so a null check cannot tell \"not given\" from \"given the default\".\n");
        code.Append("    private global::Community.Zabbix.ZabbixHostInterfaceRequest[]? MergeInterfaces(global::Community.Zabbix.ZabbixHostInterfaceRequest[]? interfaces)\n    {\n");
        code.Append("        if (!MyInvocation.BoundParameters.ContainsKey(nameof(InterfaceType)) && !MyInvocation.BoundParameters.ContainsKey(nameof(InterfaceIp))\n");
        code.Append("            && !MyInvocation.BoundParameters.ContainsKey(nameof(InterfaceDns)) && !MyInvocation.BoundParameters.ContainsKey(nameof(InterfacePort))\n");
        code.Append("            && !MyInvocation.BoundParameters.ContainsKey(nameof(UseIp)))\n");
        code.Append("            return interfaces;\n");
        code.Append("        global::Community.Zabbix.ZabbixInterfaceType type = InterfaceType ?? global::Community.Zabbix.ZabbixInterfaceType.Agent;\n");
        // -InterfaceDns alone must not create useip=1 on 127.0.0.1, an interface that never reaches the named host. A DNS name without an address means connect by DNS, unless -UseIp says otherwise.
        code.Append("        bool useIp = MyInvocation.BoundParameters.ContainsKey(nameof(UseIp)) ? UseIp.IsPresent : InterfaceDns is null || InterfaceIp is not null;\n");
        code.Append("        var shorthand = new global::Community.Zabbix.ZabbixHostInterfaceRequest\n        {\n");
        code.Append("            Type = type,\n            Main = global::Community.Zabbix.ZabbixInterfacePriority.Primary,\n            UseIp = useIp ? global::Community.Zabbix.ZabbixHostInterfaceUseIp.Ip : global::Community.Zabbix.ZabbixHostInterfaceUseIp.Dns,\n");
        code.Append("            Ip = InterfaceIp ?? (useIp ? \"127.0.0.1\" : string.Empty),\n            Dns = InterfaceDns ?? string.Empty,\n");
        code.Append("            Port = InterfacePort is null ? (type == global::Community.Zabbix.ZabbixInterfaceType.Snmp ? 161 : 10050) : global::Community.Zabbix.ZabbixPort.Parse(InterfacePort),\n");
        code.Append("        };\n");
        code.Append("        return interfaces is null ? [shorthand] : [.. interfaces, shorthand];\n    }\n");
    }

    private void EmitSet(StringBuilder code, ServiceDescription service, EntityInfo entity)
    {
        TypeDescription? request = _byObjectRole[(service.Object, "request")].FirstOrDefault(t => t.Method == service.Object + ".update");
        if (request is null) { _problems.Add(new(ZabbixDiagnostics.NotGenerated, "update is a method of " + service.Object + " but no *UpdateRequest is described", service.Object, null)); return; }
        FieldDescription? ownId = request.Fields.FirstOrDefault(f => f.Kind == "id" && f.Wire == entity.IdField) ?? request.Fields.FirstOrDefault(f => f.Kind == "id");
        if (ownId is null) { _problems.Add(new(ZabbixDiagnostics.NotGenerated, "the update request of " + service.Object + " carries no id field", service.Object, null)); return; }

        string entityType = "global::Community.Zabbix." + Prefixed(entity.Read.Name);
        string requestType = "global::Community.Zabbix." + Prefixed(request.Name);
        List<(FieldDescription Field, ParamInfo Info)> parameters = Superseding([.. request.Fields.Where(f => f != ownId).Select(f => (f, Param(f, request.Name))).Where(p => p.Item2 is not null).Select(p => (p.f, p.Item2!))]);
        HashSet<string> siblingNames = [.. parameters.Select(p => p.Field.Name), ownId.Name];
        List<(FieldDescription Field, ParamInfo Info)> escapableRequired = [.. parameters.Where(p => IsEscapableRequired(service.Object, p.Field))];

        EmitSetHelp(code, service, entity, parameters, siblingNames);
        code.Append("[global::System.Management.Automation.Cmdlet(global::System.Management.Automation.VerbsCommon.Set, \"Zbx").Append(entity.Noun).Append("\", SupportsShouldProcess = true, ConfirmImpact = global::System.Management.Automation.ConfirmImpact.Medium)]\n");
        code.Append("[global::System.Management.Automation.OutputType(typeof(").Append(entityType).Append("))]\n");
        code.Append("public sealed class Set").Append(entity.Noun).Append("Command : ZabbixSetCmdlet<").Append(entityType).Append(", ").Append(requestType).Append(">\n{\n");
        code.Append("    /// <summary>The ").Append(Prose(entity.Noun)).Append("(s) to change or delete: ids, names or objects, from the pipeline or by position. -Id is an alias.</summary>\n");
        code.Append("    [global::System.Management.Automation.Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]\n");
        code.Append("    [global::System.Management.Automation.Alias(\"Id\")]\n");
        code.Append("    [ZabbixReference<").Append(entityType).Append(">]\n    [ZabbixCompleter<").Append(entityType).Append(">]\n");
        code.Append("    public override ZabbixReference<").Append(entityType).Append(">[]? InputObject { get; set; }\n\n");
        // After -InputObject (position 0), the new name takes position 1
        // (Set-ZbxHostGroup 5 'SQL servers').
        FieldDescription? rename = parameters.Select(p => p.Field).FirstOrDefault(f => f.Wire == entity.NameField && f.Kind == "string");
        foreach ((FieldDescription field, ParamInfo info) in parameters) EmitParameter(code, service.Object, field, info, siblingNames, position: field == rename ? 1 : null);
        EmitPropertiesParameter(code);
        List<(FieldDescription Field, ParamInfo Info)> references = [.. parameters.Where(p => p.Info.Reference is not null)];
        if (references.Count > 0)
        {
            foreach ((FieldDescription field, ParamInfo info) in references)
                code.Append("    private global::Community.Zabbix.").Append(Prefixed(info.Reference!.RecordType)).Append("[]? _resolved").Append(field.Name).Append(";\n");
            code.Append('\n');
            code.Append("    /// <inheritdoc/>\n    protected override async global::System.Threading.Tasks.Task PrepareRequestAsync(global::System.Threading.CancellationToken cancellationToken)\n    {\n");
            foreach ((FieldDescription field, ParamInfo info) in references)
            {
                ReferenceListInfo r = info.Reference!;
                string referenceEntityType = "global::Community.Zabbix." + Prefixed(r.EntityReadType);
                string referenceRecordType = "global::Community.Zabbix." + Prefixed(r.RecordType);
                string property = CmdletPropertyName(service.Object, field);
                code.Append("        if (").Append(property).Append(" is not null) _resolved").Append(field.Name)
                    .Append(" = [.. global::System.Linq.Enumerable.Select(await ResolveFieldAsync<").Append(referenceEntityType).Append(">(").Append(property)
                    .Append(", nameof(").Append(property).Append("), cancellationToken).ConfigureAwait(false), id => new ").Append(referenceRecordType).Append(" { ").Append(r.RecordIdMember).Append(" = id })];\n");
            }
            code.Append("    }\n\n");
        }
        code.Append("    /// <inheritdoc/>\n    protected override ").Append(requestType).Append(" BuildRequest(ulong id)\n    {\n");
        EmitRequiredFieldValidation(code, "        ", service.Object, requestType, escapableRequired);
        code.Append("        return global::Community.Zabbix.PowerShell.ZabbixPropertyOverrides.Apply(new ").Append(requestType).Append("\n        {\n");
        code.Append("            ").Append(ownId.Name).Append(" = id,\n");
        foreach ((FieldDescription field, ParamInfo info) in parameters)
            Obsolete(code, field).Append("            ").Append(field.Name).Append(" = ").Append(info.Reference is not null ? "_resolved" + field.Name : Assign(service.Object, field, info, CmdletPropertyName(service.Object, field))).Append(",\n").Append(Restored(field));
        code.Append("        }, AdditionalProperty);\n    }\n\n");
        code.Append("    /// <inheritdoc/>\n    protected override global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<ulong>> UpdateAsync(ZabbixClient client, global::System.Collections.Generic.IReadOnlyList<").Append(requestType).Append("> requests, global::System.Threading.CancellationToken cancellationToken)\n");
        code.Append("        => client.").Append(service.Property).Append(".UpdateAsync(requests, cancellationToken);\n}\n\n");
    }

    private void EmitRemove(StringBuilder code, ServiceDescription service, EntityInfo entity)
    {
        string entityType = "global::Community.Zabbix." + Prefixed(entity.Read.Name);
        EmitRemoveHelp(code, service, entity);
        code.Append("[global::System.Management.Automation.Cmdlet(global::System.Management.Automation.VerbsCommon.Remove, \"Zbx").Append(entity.Noun).Append("\", SupportsShouldProcess = true, ConfirmImpact = global::System.Management.Automation.ConfirmImpact.High)]\n");
        code.Append("public sealed class Remove").Append(entity.Noun).Append("Command : ZabbixRemoveCmdlet<").Append(entityType).Append(">\n{\n");
        code.Append("    /// <summary>The ").Append(Prose(entity.Noun)).Append("(s) to change or delete: ids, names or objects, from the pipeline or by position. -Id is an alias.</summary>\n");
        code.Append("    [global::System.Management.Automation.Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]\n");
        code.Append("    [global::System.Management.Automation.Alias(\"Id\")]\n");
        code.Append("    [ZabbixReference<").Append(entityType).Append(">]\n    [ZabbixCompleter<").Append(entityType).Append(">]\n");
        code.Append("    public override ZabbixReference<").Append(entityType).Append(">[]? InputObject { get; set; }\n\n");
        code.Append("    /// <inheritdoc/>\n    protected override global::System.Threading.Tasks.Task DeleteAsync(ZabbixClient client, global::System.Collections.Generic.IReadOnlyList<ulong> ids, global::System.Threading.CancellationToken cancellationToken)\n");
        code.Append("        => client.").Append(service.Property).Append(".DeleteAsync(ids, cancellationToken);\n}\n\n");
    }

    // The name of a reference-list field's raw-id twin (hosts -> HostIds): the singular of the field name plus "Ids",
    // the name a removed raw-id list carries beside the reference list that replaced it (Superseding).
    private static string ReferenceAlias(string fieldName)
        => (fieldName.EndsWith("s", StringComparison.Ordinal) ? fieldName[..^1] : fieldName) + "Ids";

    // An object whose requests carry a visible name beside a technical one (cmdlets.objects.<object>.names: a host's
    // "name" beside its "host") takes the technical name as -Name, the name Get-Zbx<Object> -Name selects by and the
    // records call TechnicalName, and the visible name as -DisplayName (VisibleName on the records). Every other field
    // takes its member's name.
    private string CmdletPropertyName(string? serviceObject, FieldDescription field)
    {
        CmdletObject? shape = CmdletsOf(serviceObject);
        string name = shape?.Names is { } names && field.Kind == "string"
            ? field.Wire == names.Technical ? "Name" : field.Wire == names.Display ? "DisplayName" : field.Name
            : Singular(field);
        if (!HidesCmdletMember(name)) return name;
        // A reference list whose singular is a PSCmdlet member (hosts: -Host) is <Member>Reference, as the Get- filter is.
        return field.Kind.StartsWith("list:object:", StringComparison.Ordinal) && TryReferenceList(field.Kind.Substring(12), out _) ? name + "Reference" : UnhiddenName(shape, field, name);
    }

    // SD03 "Use Singular Parameter Names", arrays included: a list field's parameter takes the singular of its member's
    // name (Tags: -Tag, Dependencies: -Dependency, Addresses: -Address); the record member keeps the plural.
    private static string Singular(FieldDescription field)
    {
        string name = field.Name;
        // the hosts a trapper allows are one text in the shell, as Zabbix writes them, so their parameter keeps the plural
        if (!field.Kind.StartsWith("list:", StringComparison.Ordinal) || field.Kind == ZabbixTypedFields.Peers) return name;
        if (name.EndsWith("ies", StringComparison.Ordinal)) return name.Substring(0, name.Length - 3) + "y";
        if (name.EndsWith("sses", StringComparison.Ordinal) || name.EndsWith("xes", StringComparison.Ordinal) || name.EndsWith("ches", StringComparison.Ordinal)) return name.Substring(0, name.Length - 2);
        if (name.EndsWith("Children", StringComparison.Ordinal)) return name.Substring(0, name.Length - 3);
        return name.EndsWith("s", StringComparison.Ordinal) && !name.EndsWith("ss", StringComparison.Ordinal) && !name.EndsWith("us", StringComparison.Ordinal) ? name.Substring(0, name.Length - 1) : name;
    }

    // A parameter named after one of PSCmdlet's own members (-Host is the host a command filters or creates on;
    // PSCmdlet.Host is the PowerShell host) would hide it with `new`. The property takes a name of its own and the
    // parameter keeps its spelling as an alias: the name the description gives the field (cmdlets.objects.<object>.unhide:
    // the technical name of a host is TechnicalName, as on the read record), else its own name followed by Value; a
    // reference filter is <Member>Reference.
    private static bool HidesCmdletMember(string name) => Array.IndexOf(s_cmdletMembers, name) >= 0;

    private static string UnhiddenName(CmdletObject? shape, FieldDescription field, string name)
        => shape?.Unhidden.FirstOrDefault(u => u.Wire == field.Wire)?.Property ?? name + "Value";

    private static string FilterProperty(string concept) => HidesCmdletMember(concept) ? concept + "Reference" : concept;

    // The attributes a parameter carries to take its field's value in the form the shell writes, one per line: a user
    // macro's or an LLD macro's name (NAME -> {$NAME}, {#NAME}), a map's named colors (DarkGray -> 333333), a widget's
    // type (ProblemsBySeverity -> problemsbysv), each a transformation that also takes the wire value, and their
    // completions. A builder's forms are its own: a generated New-/Set- of the same fields takes records (-Shape,
    // -Widget), whose values are the wire's.
    private static string ValueFormAttributes(EquatableArray<ValueForm>? forms, string? wire)
    {
        ValueForm? form = forms?.FirstOrDefault(f => f.Wire == wire);
        return form is null ? string.Empty : string.Concat(form.Attributes.Select(attribute => "    [" + attribute + "]\n"));
    }

    private void EmitParameter(StringBuilder code, string? serviceObject, FieldDescription field, ParamInfo info, HashSet<string>? siblingNames = null, string? conceptKey = null, int? position = null, bool create = false)
    {
        BuilderDescription? builder = serviceObject is null && conceptKey is not null ? _builders.GetValueOrDefault(conceptKey) : null;
        string? condition = serviceObject is not null && _requiredWhen.TryGetValue((serviceObject, field.Wire), out string? c) ? c
            : builder?.OwnerSupplied.FirstOrDefault(note => note.Wire == field.Wire)?.Text;
        bool required = field.Required && condition is null;
        // A generated New-/Set- (serviceObject not null) always carries -AdditionalProperty, the escape hatch that can supply
        // ANY wire field, including a required one — so a required field's own named parameter is never the only way
        // to supply it there, and Mandatory=true would wrongly forbid skipping the named parameter and using
        // -AdditionalProperty instead (New-ZbxProxy -AdditionalProperty @{ operating_mode = ... } rather than -OperatingMode). EmitRequiredFieldValidation enforces "required" instead, once -AdditionalProperty is merged, with
        // a clear error naming the field. A nested-record builder (serviceObject null: New-ZbxTag, ...) carries no
        // -AdditionalProperty at all — its own required field's parameter is the ONLY way to supply it, so Mandatory stays.
        bool mandatory = required && serviceObject is null;
        string summary = Doc(DocSentences.Field(field.Summary, field.Name, field.Wire), field.Wire);
        string cmdletProperty = CmdletPropertyName(serviceObject, field);
        code.Append("    /// <summary>").Append(summary);
        if (condition is not null) code.Append(' ').Append(Doc(char.ToUpperInvariant(condition[0]) + condition[1..] + ".", condition));
        if (info.Reference is not null) code.Append(" Ids, names, or objects (e.g. from Get-Zbx").Append(info.Reference.EntityNoun).Append(").");
        if (info.Json) code.Append(" Takes a hashtable, an ordered dictionary, a [pscustomobject] or an array of them, JSON text, or a JsonElement.");
        code.Append("</summary>\n");
        // Every generated ZabbixReference<T>[] parameter carries ValueFromPipelineByPropertyName = true, so a piped
        // Get-Zbx<Entity> result's own id property binds by name (New-/Set-'s equivalent of the Get- filter
        // parameters above).
        // The object a New- creates something ON (an item's, a discovery rule's, a macro's hostid) comes from the
        // pipeline — Get-ZbxHost | New-ZbxItem ..., and a template's TemplateId the same.
        bool owner = create && info.Reference is null && info.IsId && field.Wire == "hostid";
        // A generated New-/Set- takes every field by property name, so an Import-Csv row or a [pscustomobject] with Name,
        // InterfaceIp, ... columns creates or changes one object per row; the base unbinds what a piped Zabbix record
        // bound that is not its own (ZabbixPipedRecord). A secret is never taken from a piped object: a SecureString
        // has no conversion from a column's text, and a column of plaintext secrets is what the type exists to prevent.
        bool byPropertyName = info.Reference is not null || owner || (serviceObject is not null && !info.Secret);
        code.Append("    [global::System.Management.Automation.Parameter(Mandatory = ").Append(mandatory ? "true" : "false")
            .Append(position is int at ? ", Position = " + at.ToString(global::System.Globalization.CultureInfo.InvariantCulture) : string.Empty)
            .Append(byPropertyName ? ", ValueFromPipelineByPropertyName = true" : string.Empty).Append(")]\n");
        // Every alias of the parameter, in one [Alias] (PowerShell takes one per parameter). An alias is kept only where it
        // binds something a parameter name cannot: a piped template's TemplateId to the hostid a New- creates on, the
        // spelling of a name that would hide a PSCmdlet member (-Host), and the one name host-like objects share for the
        // same field (-Name for the technical name, -DisplayName for the visible one).
        List<string> aliases = [];
        if (owner && siblingNames is not null && !siblingNames.Contains("TemplateId"))
            aliases.Add("TemplateId");
        // The technical name keeps the wire's word (-Host), the one Send-ZbxData and the agent commands' -HostName say.
        NameFields? names = info.Reference is null ? CmdletsOf(serviceObject)?.Names : null;
        if (names is not null && field.Wire == names.Technical && field.Kind == "string") aliases.Add("Host");
        if (cmdletProperty != field.Name && HidesCmdletMember(field.Name)) aliases.Insert(0, field.Name);
        if (cmdletProperty != Singular(field) && HidesCmdletMember(Singular(field))) aliases.Insert(0, Singular(field));
        aliases.RemoveAll(alias => string.Equals(alias, cmdletProperty, StringComparison.OrdinalIgnoreCase));
        if (aliases.Count > 0)
            code.Append("    [global::System.Management.Automation.Alias(").Append(string.Join(", ", aliases.Distinct(StringComparer.OrdinalIgnoreCase).Select(Lit))).Append(")]\n");
        if (info.Reference is not null)
        {
            string entityType = "global::Community.Zabbix." + Prefixed(info.Reference.EntityReadType);
            code.Append("    [ZabbixReference<").Append(entityType).Append(">]\n");
            code.Append("    [ZabbixCompleter<").Append(entityType).Append(">]\n");
        }
        code.Append(ValueFormAttributes(serviceObject is not null ? CmdletsOf(serviceObject)?.ValueForms : builder?.ValueForms, field.Wire));
        if (info.Flags) code.Append("    [ZabbixFlags<").Append(info.Type.TrimEnd('?')).Append(">]\n");
        if (info.Clock) code.Append("    [global::Community.Zabbix.PowerShell.ZabbixClock]\n");
        if (info.TimeOfDay) code.Append("    [global::Community.Zabbix.PowerShell.ZabbixTimeOfDay]\n");
        if (info.Json) code.Append("    [ZabbixJson]\n");
        // info.Type is "global::Community.Zabbix.<Record>[]?" here (the "list:object:" branch of Param()) — strip the trailing
        // array-and-nullable marker to get the element type ZabbixNameValueListAttribute<TRecord> is generic over.
        if (info.PairName is not null) code.Append("    [ZabbixNameValueList<").Append(info.Type[..^"[]?".Length]).Append(">(").Append(Lit(info.PairName)).Append(")]\n");
        bool asSwitch = SwitchOf(serviceObject, field) is not null;
        if (info.Type == "string?") code.Append(ZabbixApiEmitter.UrlSuppression(cmdletProperty, "    "));
        code.Append("    public ").Append(asSwitch ? "global::System.Management.Automation.SwitchParameter" : info.Type).Append(' ').Append(cmdletProperty).Append(" { get; set; }\n\n");
    }

    // A two-member 0/1 enum taken as a bare switch (New-ZbxTrigger -ManualClose), as the description's
    // cmdlets.objects.<object>.switches names it: the switch sends the first member when present, the second when given
    // as -X:$false, and nothing when not given. Not a rule over every 0/1 enum: most (Status Enabled/Disabled...) stay
    // enums.
    private SwitchField? SwitchOf(string? serviceObject, FieldDescription field)
        => CmdletsOf(serviceObject)?.Switches.FirstOrDefault(s => s.Wire == field.Wire);

    /// <summary><c>HostGroup</c> → <c>host group</c>: a cmdlet noun as prose, for the emitted help text only.</summary>
    /// <param name="noun">The noun without its <c>Zbx</c> prefix.</param>
    private static string Prose(string noun)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < noun.Length; i++)
        {
            char c = noun[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(noun[i - 1]) || (i + 1 < noun.Length && char.IsLower(noun[i + 1])))) builder.Append(' ');
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    /// <summary>
    /// The generic escape hatch: a hashtable merged onto the built request last
    /// (<c>Zabbix.PowerShell.ZabbixPropertyOverrides.Apply</c>), so it wins over every named parameter above. A key that
    /// names a property of the request (case-insensitively, or by its wire name) sets it; any other key goes into the
    /// request's <c>AdditionalFields</c> bag (every sendable record has one, <see cref="ZabbixApiEmitter"/>) and is sent
    /// as a wire field the model does not expose.
    /// </summary>
    private static void EmitPropertiesParameter(StringBuilder code)
    {
        code.Append("    /// <summary>Extra fields merged onto the request last, winning over every parameter: keys are the API's field\n");
        code.Append("    /// names (or the request's property names, case-insensitively); a nested value may be a hashtable or an array.</summary>\n");
        code.Append("    [global::System.Management.Automation.Parameter]\n");
        // settable, as the hand-written cmdlets' -AdditionalProperty: PowerShell's binder assigns a parameter through its setter
        code.Append("    public global::System.Collections.Hashtable? AdditionalProperty { get; set; }\n\n");
    }

    // ----- field kinds --------------------------------------------------------------------------------------

    // IsStruct: the parameter's non-nullable form is a value type (enum, int, …), so a required field is unwrapped
    // with .Value, never the null-forgiving operator (which does not convert Nullable<T> to T: CS0266).
    // Reference: set when the field is a nested request record whose only member is an id — the
    // parameter becomes ZabbixReference<TEntity>[] instead of the raw nested-record array. Flags: set when the
    // field is a [Flags] enum — the parameter gets a ZabbixFlagsAttribute<TEnum> transform so an array of
    // names or a bitmask integer binds to the scalar Nullable<TEnum>. PairName: the name wire field ("tag" or "name") when the field is a list of a nested request record with
    // exactly two string members, that name and "value" (tags; webhook parameters, headers, variables and
    // query fields) — the parameter gets a ZabbixNameValueListAttribute<TRecord> transform so a flat
    // hashtable-of-name-to-value shape binds too, beside the already-working typed array.
    // Json: an untyped JSON field (kind "json"), which the shell gives as a hashtable, an object, an array or JSON text —
    // the parameter gets the ZabbixJsonAttribute transform, because PowerShell has no conversion to JsonElement.
    private sealed record ParamInfo(string Type, bool Secret, bool IsId, bool IsStruct, ReferenceListInfo? Reference = null, bool Flags = false, string? PairName = null, bool Json = false, bool Clock = false, bool TimeOfDay = false, string? Parse = null);

    /// <summary>A nested single-id request record recognized as a reference to an entity the generated entity table already knows.</summary>
    /// <param name="EntityObject">The entity's API object name (<c>hostgroup</c>).</param>
    /// <param name="EntityReadType">The entity's read-model type name (<c>ZabbixHostGroup</c> or, undecorated, <c>HostGroup</c>).</param>
    /// <param name="EntityNoun">The entity's cmdlet noun (<c>HostGroup</c>, as in <c>Get-ZbxHostGroup</c>).</param>
    /// <param name="RecordType">The nested request record's type name (<c>HostGroupRequest</c>).</param>
    /// <param name="RecordIdMember">The nested request record's own id member name (<c>GroupId</c>).</param>
    private sealed record ReferenceListInfo(string EntityObject, string EntityReadType, string EntityNoun, string RecordType, string RecordIdMember);

    private bool TryReferenceList(string recordName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ReferenceListInfo? info)
    {
        info = null;
        if (!_types.TryGetValue(recordName, out TypeDescription? record) || record.Fields.Length != 1 || record.Fields[0].Kind != "id") return false;
        FieldDescription idField = record.Fields[0];
        string? entityObject = EntityObjectFor(recordName, idField.Wire);
        if (entityObject is null || !_entityByObject.TryGetValue(entityObject, out EntityInfo? entity)) return false;
        info = new ReferenceListInfo(entityObject, entity.Read.Name, entity.Noun, recordName, idField.Name);
        return true;
    }

    // A nested request record shaped like a tag or a name-value pair: a "tag" or "name" wire string field and a "value"
    // wire string field, nothing else (the shared ZabbixTagRequest; webhook parameters, headers, variables and query
    // fields).
    private string? PairNameOf(string recordName)
        => _types.TryGetValue(recordName, out TypeDescription? record) && record.Fields.Length == 2
            && record.Fields.All(f => f.Kind == "string")
            && record.Fields.Any(f => f.Wire == "value")
            && record.Fields.FirstOrDefault(f => f.Wire is "tag" or "name") is { } name
                ? name.Wire
                : null;

    private ParamInfo? Param(FieldDescription field, string record)
    {
        bool secret = s_secretName.IsMatch(field.Wire) || s_secretName.IsMatch(field.Name);
        // ModuleShapeTests.A_secret_is_never_a_positional_string flags any property whose name matches the secret
        // pattern, whatever its type: for a "string" field that is exactly the point (secret ⇒ SecureString below),
        // but a non-string field can share the pattern by naming accident (MediaTypeCreateRequest.TokensStatus is an
        // int flag, not a credential — "token" is a substring of "Tokens"). A field like that cannot be typed
        // SecureString, so it is left out rather than shipped mistyped or breaking the shared invariant.
        if (secret && field.Kind != "string")
        {
            _problems.Add(new(ZabbixDiagnostics.NotGenerated, "'" + field.Wire + "' is not a string but its name matches the secret pattern; left out to avoid a positional-secret false positive", record, field.Wire));
            return null;
        }
        switch (field.Kind)
        {
            case "id": return new("ulong?", false, true, true);
            case "clock": return new("global::System.DateTimeOffset?", false, false, true, Clock: true);
            case "date": return new("global::System.DateOnly?", false, false, true);
            case "timeofday": return new("global::System.TimeOnly?", false, false, true, TimeOfDay: true);
            case "string": return secret ? new("global::System.Security.SecureString?", true, false, false) : new("string?", false, false, false);
            case "int": return new("int?", false, false, true);
            case "long": return new("long?", false, false, true);
            case "ulong": return new("ulong?", false, false, true);
            case "double": return new("double?", false, false, true);
            case "bool" or "apibool": return new("bool?", false, false, true);
            case "json": return new("global::System.Text.Json.JsonElement?", false, false, true, Json: true);
            // a field of a typed grammar keeps the text Zabbix spells it in, as the shell writes it (-TrapperHosts
            // '10.0.0.0/8,::1'), and the record's type parses it
            case ZabbixTypedFields.Interval: return new("string?", false, false, false, Parse: "global::Community.Zabbix.ZabbixInterval.Parse");
            case ZabbixTypedFields.Port: return new("string?", false, false, false, Parse: "global::Community.Zabbix.ZabbixPort.Parse");
            case ZabbixTypedFields.Duration: return new("string?", false, false, false, Parse: "global::Community.Zabbix.ZabbixDuration.Parse");
            case ZabbixTypedFields.Peers: return new("string?", false, false, false, Parse: "global::Community.Zabbix.ZabbixPeer.ParseList");
        }
        if (field.Kind.StartsWith("enum:", StringComparison.Ordinal))
        {
            string name = field.Kind[5..];
            return IsKnownEnum(name)
                ? new("global::Community.Zabbix." + Prefixed(name) + "?", false, false, true, Flags: _enums.TryGetValue(name, out EnumDescription? e) && e.Flags)
                : null;
        }
        if (field.Kind.StartsWith("object:", StringComparison.Ordinal))
        {
            string name = field.Kind[7..];
            return _types.ContainsKey(name) ? new("global::Community.Zabbix." + Prefixed(name) + "?", false, false, false) : null;
        }
        if (field.Kind.StartsWith("list:object:", StringComparison.Ordinal))
        {
            string name = field.Kind[12..];
            if (!_types.ContainsKey(name)) return null;
            if (TryReferenceList(name, out ReferenceListInfo? reference))
                return new("global::Community.Zabbix.PowerShell.ZabbixReference<global::Community.Zabbix." + Prefixed(reference.EntityReadType) + ">[]?", false, false, false, reference);
            return new("global::Community.Zabbix." + Prefixed(name) + "[]?", false, false, false, PairName: PairNameOf(name));
        }
        if (field.Kind == "list:string") return new("string[]?", false, false, false);
        if (field.Kind == "list:id") return new("ulong[]?", false, false, false);
        _problems.Add(new(ZabbixDiagnostics.NotGenerated, "'" + field.Kind + "' is not a kind the cmdlet generator turns into a parameter", record, field.Wire));
        return null;
    }

    private bool IsKnownEnum(string name) => api.Enums.Any(e => e.Name == name);

    private string Assign(string? serviceObject, FieldDescription field, ParamInfo info, string? cmdletProperty = null)
    {
        string prop = cmdletProperty ?? field.Name;
        if (SwitchOf(serviceObject, field) is { } members)
        {
            string enumType = info.Type.TrimEnd('?');
            return "MyInvocation.BoundParameters.ContainsKey(nameof(" + prop + ")) ? (" + prop + ".IsPresent ? " + enumType + "." + members.On + " : " + enumType + "." + members.Off + ") : null";
        }
        if (info.Reference is not null)
        {
            // Resolved in one .get for every name among the references (ResolveAsync short-circuits to zero network
            // calls when every reference already carries an id — the common @($g.GroupId)/piped Get- objects shape), then mapped back onto the nested request record New- already sent.
            string entityType = "global::Community.Zabbix." + Prefixed(info.Reference.EntityReadType);
            string recordType = "global::Community.Zabbix." + Prefixed(info.Reference.RecordType);
            // All of them or no request (ResolveFieldAsync): a field sent with only the references
            // that resolved says less than was asked.
            // A required reference field left unbound is for -AdditionalProperty to supply (EmitRequiredFieldValidation has checked
            // one of the two is there): the placeholder is overwritten by ZabbixPropertyOverrides.Apply.
            return prop + " is null ? " + (field.Required ? "null!" : "null") + " : [.. global::System.Linq.Enumerable.Select(await ResolveFieldAsync<" + entityType + ">(" + prop
                + ", nameof(" + prop + "), cancellationToken).ConfigureAwait(false), id => new " + recordType + " { " + info.Reference.RecordIdMember + " = id })]";
        }
        // Only where the record's member is `required` (the create request): on the update request the same field is
        // optional, and the placeholder below would CHANGE it (Set-ZbxItem -Name x would send master_itemid "0", which
        // detaches a dependent item from its master).
        bool conditional = serviceObject is not null && field.Required && _requiredWhen.ContainsKey((serviceObject, field.Wire));
        if (conditional)
        {
            // ZabbixApiEmitter mirrors the schema's flat (unconditionally-true) "required" flag onto the request
            // record itself (a C# `required` member), so the record still demands a value even though the cmdlet
            // parameter above is deliberately not Mandatory. Falling back to the API's own documented default
            // (CItemGeneral.php's getValidationRules: master_itemid defaults to 0, url/snmp_oid to "") means an
            // unbound conditional parameter sends exactly what the API would have filled in itself, not a null the
            // `required` member forbids meaning and the null-forgiving operator would otherwise push onto the wire.
            return info.IsId ? prop + " ?? 0UL" : prop + " ?? string.Empty";
        }
        bool bang = field.Required && field.Kind is not ("bool" or "apibool");
        // the text a typed field's parameter takes, parsed; a required one that's unbound has the placeholder below
        if (info.Parse is { } parse)
            return prop + " is null ? " + (!bang ? "null" : field.Kind == ZabbixTypedFields.Peers ? "[]" : "default") + " : " + parse + "(" + prop + ")";
        // On a generated New-/Set- (serviceObject not null), a required field's own parameter is not Mandatory
        // (EmitParameter) — -AdditionalProperty can always supply it instead (as for New-ZbxProxy -OperatingMode) — so the value here can genuinely be unbound (null) even though the
        // record's own member is `required`. EmitRequiredFieldValidation (called before this expression runs) is
        // what actually enforces "required," with a clear error naming the field; the fallback below just avoids
        // building a request that would throw constructing it (id: "!.ToString()" NREs on null; struct: ".Value"
        // throws InvalidOperationException on null) — -AdditionalProperty overwrites the placeholder right after
        // (ZabbixPropertyOverrides.Apply) whenever it supplies the field, matching "wins over every parameter above."
        bool escapable = bang && serviceObject is not null;
        if (info.IsId) return escapable ? prop + " ?? 0UL" : (bang ? prop + ".Value" : prop);
        if (info.Secret)
            return bang
                ? "new global::System.Net.NetworkCredential(string.Empty, " + prop + "!).Password"
                : prop + " is null ? null : new global::System.Net.NetworkCredential(string.Empty, " + prop + ").Password";
        if (!bang) return prop;
        if (escapable) return info.IsStruct ? prop + " ?? default" : prop + "!";
        return info.IsStruct ? prop + ".Value" : prop + "!";
    }

    // The same "required, but escapable via -AdditionalProperty" test EmitParameter uses to decide Mandatory,
    // reused by EmitNew/EmitSet to know which fields need EmitRequiredFieldValidation.
    private bool IsEscapableRequired(string serviceObject, FieldDescription field)
        => field.Required && !_requiredWhen.ContainsKey((serviceObject, field.Wire));

    // "Mandatory=false, validated after -AdditionalProperty is merged" needs the check to run before the value
    // is used to build the request — MyInvocation.BoundParameters (was the named parameter itself bound this call?)
    // or ZabbixPropertyOverrides.SuppliesWireField (does -AdditionalProperty carry this wire field, by its C# name or its
    // own wire name?). Checking presence, not the resulting value, avoids a false positive on a struct field whose
    // valid value IS the type's default (e.g. an enum's zero member) — Assign()'s "?? default" fallback makes that
    // value indistinguishable from "unset" if the check inspected the built request instead.
    private void EmitRequiredFieldValidation(StringBuilder code, string indent, string serviceObject, string requestType, IReadOnlyList<(FieldDescription Field, ParamInfo Info)> escapableRequired)
    {
        foreach ((FieldDescription field, _) in escapableRequired)
        {
            string cmdletProperty = CmdletPropertyName(serviceObject, field);
            code.Append(indent).Append("if (!MyInvocation.BoundParameters.ContainsKey(nameof(").Append(cmdletProperty)
                .Append(")) && !global::Community.Zabbix.PowerShell.ZabbixPropertyOverrides.SuppliesWireField(AdditionalProperty, nameof(").Append(requestType).Append('.').Append(field.Name).Append("), ").Append(Lit(field.Wire)).Append("))\n");
            code.Append(indent).Append("    throw new global::Community.Zabbix.PowerShell.ZabbixParameterException(\"ZabbixRequiredFieldMissing\", nameof(").Append(cmdletProperty)
                .Append("), ").Append(Lit("'" + field.Wire + "' (-" + cmdletProperty + ") is required: supply it directly or via -AdditionalProperty.")).Append(");\n");
        }
    }

    // ----- nested-record builders ------------------------------------------------------------------------------

    private string EmitNestedBuilders()
    {
        var code = new StringBuilder(Header("Nested-record builders: each returns a Hashtable keyed by the modelled record's wire names (case-insensitive, PowerShell's own @{} convention), so it casts onto any owner's differently-typed nested record of the same shape and reads back as $c.conditiontype, $c.lld_macro."));
        code.Append("namespace Community.Zabbix.PowerShell.Commands.Generated;\n\n");
        foreach (BuilderDescription builder in api.CmdletShape.Builders)
        {
            (string concept, string recordName, string noun, string use, string what) = (builder.Concept, builder.Record, builder.Noun, builder.Use, builder.What);
            string[] positional = [.. builder.Positional];
            if (!_types.TryGetValue(recordName, out TypeDescription? record))
            {
                _problems.Add(new(ZabbixDiagnostics.NotGenerated, "'" + recordName + "' names no record the description declares; New-Zbx" + concept + " is not generated", concept, null));
                continue;
            }
            List<(FieldDescription Field, ParamInfo Info)> parameters = [.. record.Fields.Select(f => (f, Param(f, record.Name))).Where(p => p.Item2 is not null).Select(p => (p.f, p.Item2!))];
            EmitBuilderHelp(code, concept, noun, use, what, [.. parameters.Where(p => Array.IndexOf(positional, p.Field.Wire) >= 0).OrderBy(p => Array.IndexOf(positional, p.Field.Wire)).Select(p => CmdletPropertyName(null, p.Field))]);
            code.Append("[global::System.Management.Automation.Cmdlet(global::System.Management.Automation.VerbsCommon.New, \"Zbx").Append(concept).Append("\")]\n");
            code.Append("[global::System.Management.Automation.OutputType(typeof(global::System.Collections.Hashtable))]\n");
            code.Append("public sealed class New").Append(concept).Append("Command : ZabbixCmdlet\n{\n");
            foreach ((FieldDescription field, ParamInfo info) in parameters)
                EmitParameter(code, null, field, info, conceptKey: concept, position: Array.IndexOf(positional, field.Wire) is var at and >= 0 ? at : null);
            string recordType = "global::Community.Zabbix." + Prefixed(recordName);
            // The contract the builder writes through; also what NestedBuilderWireFormTests binds each builder's output
            // back onto, generator-wide.
            code.Append("    internal static global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<").Append(recordType)
                .Append("> Contract => global::Community.Zabbix.PowerShell.ZabbixContracts.Of<").Append(recordType).Append(">();\n\n");
            code.Append("    /// <inheritdoc/>\n    protected override void ProcessRecord()\n    {\n");
            // The builder builds the typed record and writes it through the SDK's own
            // JSON contract, so every value is in the form the wire carries by construction — an enum its number, an id
            // its string, a [Flags] enum the OR'd number, an API flag "0"/"1", a nested record a wire-keyed hashtable
            // (ZabbixJsonShape.ToWireTable). Storing the parameter's CLR value instead would put an enum member and a
            // ulong into the hashtable: a stringified raw-API body would send "HostGroup", and the owner's record could
            // not bind the ulong. Only the bound fields are kept: a required member's placeholder below never leaves.
            code.Append("        var record = new ").Append(recordType).Append("\n        {\n");
            foreach ((FieldDescription field, ParamInfo info) in parameters)
                Obsolete(code, field).Append("            ").Append(field.Name).Append(" = ").Append(BuilderValue(field, info, CmdletPropertyName(null, field))).Append(",\n").Append(Restored(field));
            code.Append("        };\n");
            code.Append("        string?[] bound =\n        [\n");
            foreach ((FieldDescription field, ParamInfo _) in parameters)
                code.Append("            ").Append(CmdletPropertyName(null, field)).Append(" is null ? null : ").Append(Lit(field.Wire)).Append(",\n");
            code.Append("        ];\n");
            code.Append("        WriteObject(global::Community.Zabbix.PowerShell.ZabbixJsonShape.ToWireTable(record, Contract, bound));\n    }\n}\n\n");
        }
        return code.ToString();
    }

    // One builder parameter as its record member's value. As Assign, minus what a builder does not have: no server to
    // resolve a reference's name through (ZabbixReference.RequireId refuses one), no -AdditionalProperty to supply a required
    // member (its placeholder is dropped from the output unless the parameter was bound).
    private static string BuilderValue(FieldDescription field, ParamInfo info, string prop)
    {
        bool required = field.Required && field.Kind is not ("bool" or "apibool");
        if (info.Reference is not null)
            return prop + " is null ? null : [.. global::System.Linq.Enumerable.Select(" + prop + ", reference => new global::Community.Zabbix." + Prefixed(info.Reference.RecordType)
                + " { " + info.Reference.RecordIdMember + " = reference.RequireId(\"" + prop + "\") })]";
        if (info.IsId) return prop + (required ? " ?? 0UL" : string.Empty);
        if (info.Parse is { } parse) return prop + " is null ? null : " + parse + "(" + prop + ")";
        if (info.Secret) return prop + " is null ? null : new global::System.Net.NetworkCredential(string.Empty, " + prop + ").Password";
        if (!required) return prop;
        if (info.IsStruct) return prop + " ?? default";
        return info.Type == "string?" ? prop + " ?? string.Empty" : prop + "!";
    }

    // ----- shared -------------------------------------------------------------------------------------------

    private static string Prefixed(string name) => name.StartsWith("Zabbix", StringComparison.Ordinal) ? name : "Zabbix" + name;

    private static string Noun(string client)
    {
        string name = client.StartsWith("Zabbix", StringComparison.Ordinal) ? client[6..] : client;
        return name.EndsWith("Client", StringComparison.Ordinal) ? name[..^6] : name;
    }

    // A field the API deprecated is still a parameter: a server of the described range still takes it, and the SDK's field
    // gate refuses it where it was removed (ZabbixApiMethods.EnsureFieldSupported). Setting its [Obsolete] member is the
    // point, so the warning is off for that one line, never the file.
    private static StringBuilder Obsolete(StringBuilder code, FieldDescription field)
        => field.Deprecated is null ? code : code.Append("#pragma warning disable CS0618 // deprecated since Zabbix ").Append(field.Deprecated).Append(", still sent where the server takes it\n");

    // PSCmdlet's own members a Zabbix parameter could be named after (-Host): such a parameter's property takes another
    // name and keeps this one as an alias (CmdletPropertyName, FilterProperty), never `new`.
    private static readonly string[] s_cmdletMembers = ["Host", "Events", "InvokeCommand", "InvokeProvider", "SessionState", "MyInvocation", "PagingParameters", "ParameterSetName", "CommandRuntime", "Stopping", "CommandOrigin", "JobRepository", "JobManager"];

    private static string Restored(FieldDescription field) => field.Deprecated is null ? string.Empty : "#pragma warning restore CS0618\n";

    // Where -Id selects many records, not one object each: history's -Id is the item's (itemid), so -Limit with it reads
    // the latest N values (with -SortField clock -SortOrder Descending) — -Id and -Limit together resolve
    // to the All set, where -Id also binds. Every other generated -Id is the object's own and selects one per id.
    private bool LimitsById(string apiObject, EntityInfo entity) => CmdletsOf(apiObject)?.ManyPerId is { } wire && entity.IdField == wire;

    private static string Doc(string? summary, string fallback) => CodeText.Doc(summary, fallback);

    private static string Lit(string value) => CodeText.Literal(value);

    private static string Header(string summary) =>
        // No <auto-generated/> marker and no blanket #pragma: the cmdlets are held to the library's analyzers like
        // hand-written code (as ZabbixApiEmitter's output is).
        // The file's own note is a plain comment: a doc comment ahead of a file-scoped namespace documents nothing (CS1587).
        "// Emitted by Zabbix.Generator (ZabbixCmdletEmitter) from schema/zabbix-api.json. Do not edit.\n// " + CodeText.Plain(summary) + "\n#nullable enable\n\n";
}
