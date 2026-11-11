// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
extern alias ZabbixGenerator;

using System.Collections;
using System.Management.Automation;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZabbixGenerator::Community.Zabbix.Generator;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The pipeline matrix, generated, not sampled.
/// Every parameter of every exported command that takes pipeline input and refers to a Zabbix object (a
/// <see cref="ZabbixReference{TEntity}"/>, or a <see cref="ulong"/> naming an entity's id) is fed each input form —
/// (a) the typed entity, (b) a <c>[pscustomobject]</c> carrying the entity's id property, (c) a hashtable with that key,
/// (d) a bare id, (d') a bare name, (e) arrays of the id and of the entity, (f) an empty pipeline, (g) <c>$null</c> —
/// against a server that answers every method generically, and each form's outcome is decided from the parameter's own
/// metadata: bound means the piped id reaches the wire (and a name is resolved by one case-insensitive search first);
/// not bound means exactly <c>InputObjectNotBound</c>; empty sends no write; <c>$null</c> writes nothing. A form another
/// pipeline parameter of the same command takes instead is that parameter's case, not this one's.
/// </summary>
public sealed class PipelineMatrixTests
{
    private const string PipedId = "4711";
    private const string OtherId = "4712";
    private const string ServerId = "4242";
    private const string ServerName = "zbx-matrix";

    /// <summary>Every (command, parameter) the matrix runs, and the ones it cannot and why.</summary>
    public static TheoryData<string> Cases { get; } = [.. Matrix.All.Where(c => c.Skip is null).Select(c => c.Key)];

    [Fact]
    public void The_matrix_covers_every_reference_shaped_pipeline_parameter_or_says_why_not()
    {
        // The census: how many pipeline parameters exist, how many the matrix runs.
        IReadOnlyList<Matrix.Case> all = Matrix.All;
        string[] unexplained = [.. all.Where(c => c.Skip is { Length: 0 }).Select(c => c.Key)];

        Assert.Empty(unexplained);
        Assert.True(all.Count(c => c.Skip is null) >= 300, $"the matrix runs {all.Count(c => c.Skip is null)} cases");
        var outcomes = all.Where(c => c.Skip is null).SelectMany(c => c.Forms()).GroupBy(f => f.Outcome).ToDictionary(g => g.Key, g => g.Count());
        // Currently 1,147 forms bind the piped id, 419 are refused and 606 are another parameter's case (a record
        // carrying the id binds by value). A Get-'s -Id does not take a piped id by value (ZabbixGetCmdlet's InputObject
        // takes ids and names, and its own tests cover them), and a generated New- and Set- take every field by property
        // name beside a hidden by-value InputObject/PipedRecord (ZabbixPipedRecord). The floor below keeps a regression
        // from quietly shrinking the matrix.
        Assert.True(outcomes.GetValueOrDefault("bound") >= 1140 && outcomes.GetValueOrDefault("refused") >= 410,
            string.Join(", ", outcomes.Select(o => $"{o.Value} {o.Key}")));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_pipeline_parameter_takes_every_input_form_as_its_metadata_says(string key)
    {
        Matrix.Case matrixCase = Matrix.All.Single(c => c.Key == key);
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        // The newest version the description covers, so no method is refused as not arrived yet.
        server.On("apiinfo.version", "\"8.0.0\"").OnAny(Matrix.Answer);
        using Shell shell = Shell.Open();
        shell.Connect(server);

        List<string> violations = [];
        foreach (Matrix.Form form in matrixCase.Forms())
        {
            int before = server.Calls.Count;
            Run run = shell.Invoke(form.Script, new Dictionary<string, object?> { ["piped"] = form.Input });
            (string Method, JsonElement Params, string? Authorization)[] calls = [.. server.Calls.Skip(before)];
            violations.AddRange(form.Check(run, calls).Select(v => $"{form.Name}: {v}"));
        }

        Assert.True(violations.Count == 0, key + "\n" + string.Join('\n', violations));
    }

    /// <summary>Every <c>Get-</c> that selects by <c>-Name</c>: the matrix's name rule, enumerated.</summary>
    public static TheoryData<string> NamedGets { get; } = [.. ModuleUnderTest.AllCmdlets
        .Where(type => type.GetProperty("Name")?.GetCustomAttributes<ParameterAttribute>(true).Any(use => use.ParameterSetName == ZabbixParameterSets.ByName) == true
            && type.BaseType is { IsGenericType: true } generic && generic.GetGenericTypeDefinition() == typeof(ZabbixGetCmdlet<,>))
        .Select(type => type.GetCustomAttribute<CmdletAttribute>()!)
        .Select(cmdlet => $"{cmdlet.VerbName}-{cmdlet.NounName}")
        .Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(NamedGets))]
    public async Task A_name_that_matches_nothing_is_one_ObjectNotFound_and_a_pattern_is_none(string command)
    {
        // Get-Service's rule: Get-X 'exact' that finds nothing writes ObjectNotFound (non-terminating, naming it); a
        // name with * or ? that finds nothing writes nothing (the generic server's one object is named otherwise).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("apiinfo.version", "\"8.0.0\"").OnAny(Matrix.Answer);
        using Shell shell = Shell.Open();
        shell.Connect(server);

        // A .get that requires an owner (hostdashboard.get's hostids) is given one.
        string owner = command == "Get-ZbxHostDashboard" ? " -Host " + ServerId : string.Empty;
        Run run = shell.Invoke($"{command} -Name 'zz-none', 'zz-*', 'z?-none'{owner}; 'after'");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.Equal(ErrorCategory.ObjectNotFound, error.CategoryInfo.Category);
        Assert.StartsWith("ZabbixNameNotFound,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        // As the command bound it (Get-ZbxUserMacro writes 'zz-none' as the macro {$ZZ-NONE}).
        Assert.Contains("zz-none", error.TargetObject?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("after", run.Output[^1].ToString());
    }

    /// <summary>The matrix's model: the cases, the forms, and the generic server.</summary>
    internal static class Matrix
    {
        private static readonly Lazy<IReadOnlyList<Case>> Built = new(Build);
        private static readonly Lazy<Dictionary<string, (string Id, string Name)>> Objects = new(ObjectFields);
        private static readonly Lazy<Dictionary<string, string>> Results = new(ResultShapes);

        public static IReadOnlyList<Case> All => Built.Value;

        /// <summary>The generic server: a get answers one object of its API object, a write answers its id.</summary>
        public static string Answer(string method, JsonElement parameters)
        {
            // An export is a document with one host and one template (Copy-ZbxItem narrows and retargets it; Copy-ZbxHost
            // and Copy-ZbxTemplate rename theirs).
            if (method == "configuration.export") return JsonSerializer.Serialize("""{"zabbix_export":{"hosts":[{"host":"zbx-matrix","items":[]}],"templates":[{"template":"zbx-matrix","name":"zbx-matrix"}]}}""");
            // What the description says the method answers: "ids:<key>" an id list under that key, "text" a string.
            if (Results.Value.TryGetValue(method, out string? result))
            {
                if (result.StartsWith("ids:", StringComparison.Ordinal)) return "{\"" + result[4..] + "\":[\"" + ServerId + "\"]}";
                if (result is "string" or "text") return "\"{}\"";
            }
            string api = method[..method.IndexOf('.', StringComparison.Ordinal)];
            if (!Objects.Value.TryGetValue(api, out (string Id, string Name) fields)) return "true";
            string verb = method[(api.Length + 1)..];
            if (verb == "get")
            {
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("countOutput", out JsonElement count) && count.ValueKind == JsonValueKind.True) return "\"1\"";
                // an object named by its id (an alert, a history row) answers the id under that one field
                var row = new Dictionary<string, string> { [fields.Name] = ServerName, [fields.Id] = ServerId };
                return "[" + JsonSerializer.Serialize(row) + "]";
            }
            return "{\"" + fields.Id + "s\":[\"" + ServerId + "\"]}";
        }

        private static Dictionary<string, (string Id, string Name)> ObjectFields()
        {
            ApiDescription description = ApiDescription.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema", "zabbix-api.json")));
            var fields = new Dictionary<string, (string Id, string Name)>(StringComparer.Ordinal);
            foreach (ServiceDescription service in description.Services)
            {
                if (description.Types.FirstOrDefault(t => t.Role == "read" && t.Object == service.Object) is not { } read) continue;
                if (ZabbixCmdletEmitter.EntityIdField(service, read) is not { } id) continue;
                fields[service.Object] = (id.Wire, ZabbixCmdletEmitter.EntityNameField(service.Object, read)?.Wire ?? id.Wire);
            }
            return fields;
        }

        private static Dictionary<string, string> ResultShapes()
        {
            ApiDescription description = ApiDescription.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema", "zabbix-api.json")));
            var results = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ServiceDescription service in description.Services)
                foreach (MethodDescription method in service.Methods)
                    results[method.Api] = method.Result;
            return results;
        }

        private static IReadOnlyList<Case> Build()
        {
            var cases = new List<Case>();
            foreach (Type type in ModuleUnderTest.AllCmdlets.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                CmdletAttribute cmdlet = type.GetCustomAttribute<CmdletAttribute>()!;
                string name = cmdlet.VerbName + "-" + cmdlet.NounName;
                PipelineParameter[] pipeline = [.. PipelineParameters(type)];
                foreach (PipelineParameter parameter in pipeline)
                    cases.Add(new Case(name, type, cmdlet, parameter, pipeline));
            }
            return cases;
        }

        private static IEnumerable<PipelineParameter> PipelineParameters(Type type)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                ParameterAttribute[] uses = [.. property.GetCustomAttributes<ParameterAttribute>(true)];
                if (!uses.Any(u => u.ValueFromPipeline || u.ValueFromPipelineByPropertyName)) continue;
                string[] aliases = [.. property.GetCustomAttributes<AliasAttribute>(true).SelectMany(a => a.AliasNames)];
                yield return new PipelineParameter(property, uses, aliases, EntityOf(type, property));
            }
        }

        // The entity a parameter refers to: ZabbixReference<T>'s T; a ulong on a Get-'s -Id is the Get's entity; a
        // ulong whose name or alias is an entity's own id member (New-ZbxItem -HostId) is that entity.
        private static Type? EntityOf(Type cmdlet, PropertyInfo property)
        {
            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type.IsArray) type = type.GetElementType()!;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ZabbixReference<>)) return type.GetGenericArguments()[0];
            if (type != typeof(ulong)) return null;
            for (Type? t = cmdlet; t is not null; t = t.BaseType)
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ZabbixGetCmdlet<,>) && property.Name == "Id") return t.GetGenericArguments()[0];
            string[] names = [property.Name, .. property.GetCustomAttributes<AliasAttribute>(true).SelectMany(a => a.AliasNames)];
            return names.Select(n => n.EndsWith("Id", StringComparison.Ordinal) ? n[..^2] : null).Where(n => n is not null)
                .Select(n => typeof(ZabbixHost).Assembly.GetType("Community.Zabbix.Zabbix" + n)).FirstOrDefault(t => t is not null && Shape(t) is not null);
        }

        /// <summary>The entity's id and name members: C# property names read off the shape's wire fields.</summary>
        public static (string IdMember, string? NameMember)? Shape(Type entity)
        {
            MethodInfo tryGet = typeof(ZabbixEntities).GetMethod(nameof(ZabbixEntities.TryGet))!.MakeGenericMethod(entity);
            object?[] arguments = [null];
            if (!(bool)tryGet.Invoke(null, arguments)!) return null;
            object shape = arguments[0]!;
            string idField = (string)shape.GetType().GetProperty("IdField")!.GetValue(shape)!;
            string nameField = (string)shape.GetType().GetProperty("NameField")!.GetValue(shape)!;
            string? Member(string wire) => entity.GetProperties().FirstOrDefault(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name == wire)?.Name;
            return Member(idField) is { } idMember ? (idMember, Member(nameField)) : null;
        }

        public sealed record PipelineParameter(PropertyInfo Property, ParameterAttribute[] Uses, string[] Aliases, Type? Entity)
        {
            public string Name => Property.Name;

            public bool ByValue => Uses.Any(u => u.ValueFromPipeline);

            public bool ByPropertyName => Uses.Any(u => u.ValueFromPipelineByPropertyName);

            public bool IsReference => Entity is not null && (Property.PropertyType.IsArray ? Property.PropertyType.GetElementType()! : Property.PropertyType).IsGenericType;

            public bool Answers(string property) => ByPropertyName && (string.Equals(Name, property, StringComparison.OrdinalIgnoreCase) || Aliases.Contains(property, StringComparer.OrdinalIgnoreCase));
        }

        public sealed record Form(string Name, string Script, object? Input, Func<Run, (string Method, JsonElement Params, string? Authorization)[], IEnumerable<string>> Check)
        {
            /// <summary>What the form asserts: bound (the id reaches the wire), refused, resolved by name, nothing written, or another parameter's case.</summary>
            public string Outcome { get; init; } = "nothing written";
        }

        public sealed class Case(string command, Type type, CmdletAttribute cmdlet, PipelineParameter parameter, PipelineParameter[] pipeline)
        {
            public string Key { get; } = command + " -" + parameter.Name;

            /// <summary>Why the matrix does not run this case (empty: no reason given, a defect of the matrix); null when it runs.</summary>
            public string? Skip { get; } = Reason(type, parameter);

            private static string? Reason(Type type, PipelineParameter parameter)
            {
                if (parameter.Entity is null) return "not a reference to a Zabbix object: bound by its own command's tests";
                if (Shape(parameter.Entity) is null) return "the entity has no shape (no id member)";
                if (!typeof(ZabbixServerCmdlet).IsAssignableFrom(type)) return "talks to no API server";
                if (type.GetProperty("Server")?.PropertyType == typeof(System.Net.DnsEndPoint[]))
                    return "speaks the server's own protocol past the API (a trapper): bound by its own command's tests";
                return null;
            }

            public IEnumerable<Form> Forms()
            {
                (string idMember, string? nameMember) = Shape(parameter.Entity!)!.Value;
                string call = Call();
                object typed = Entity(PipedId);
                var custom = new PSObject();
                custom.Properties.Add(new PSNoteProperty(idMember, PipedId));
                var hashtable = new Hashtable { [idMember] = PipedId };

                // A Set- takes the object it changes by value (its -InputObject, a reference to its own entity), which a
                // [pscustomobject] gives by carrying the changed object's id beside this one (an Import-Csv row of HostId and
                // ProxyId), so its other column binds.
                Type? changed = ChangedBy(type);
                bool typedBinds = parameter.ByValue || parameter.Answers(idMember);
                // A record carrying the id by the entity's own id name binds by property name, and by value to a reference
                // parameter (ZabbixReference.TryFrom: an Import-Csv row of ids).
                bool customBinds = parameter.Answers(idMember) || (parameter.ByValue && parameter.IsReference);
                if (changed is not null && !parameter.ByValue && customBinds && Shape(changed) is { } target && target.IdMember != idMember)
                    custom.Properties.Add(new PSNoteProperty(target.IdMember, ServerId));
                bool valueBinds = parameter.ByValue;
                // A typed entity binds by property name through ANY property whose name the parameter answers to (a
                // trigger piped to New-ZbxTrigger binds its own Dependencies to -Dependency): what reaches the wire is
                // then that property, not the id, so such a form is the binder's documented behaviour, not this case's.
                string[] entityProperties = [.. parameter.Entity!.GetProperties().Select(p => p.Name)];
                bool typedByOther = !typedBinds && entityProperties.Any(parameter.Answers);
                bool typedElsewhere = typedByOther || TakenElsewhere(p => p.ByValue || entityProperties.Any(p.Answers));

                yield return Expect("a typed entity", "$piped | " + call, typed, typedBinds, typedElsewhere);
                yield return Expect("b [pscustomobject]", "$piped | " + call, custom, customBinds,
                    TakenElsewhere(p => p.Answers(idMember) || (p.ByValue && p.IsReference && Shape(p.Entity!)?.IdMember == idMember)));
                yield return Expect("c hashtable", "$piped | " + call, hashtable, bound: false, TakenElsewhere(p => false));
                yield return Expect("d bare id", "$piped | " + call, PipedId, valueBinds, TakenElsewhere(p => p.ByValue));
                if (parameter.IsReference && valueBinds && nameMember is not null && nameMember != idMember)
                    yield return new Form("d' bare name", "$piped | " + call, ServerName.ToUpperInvariant(), (run, calls) => ResolvedByName(run, calls)) { Outcome = "resolved by name" };
                yield return Expect("e array of ids", "$piped | " + call, new object[] { PipedId, OtherId }, valueBinds, TakenElsewhere(p => p.ByValue), both: true);
                // A Set- applies two records' values for the one object its written -InputObject names as running them one by
                // one would: the later wins (the piped id is sent last), so only a by-value parameter sends both.
                bool lastWins = changed is not null && !parameter.ByValue;
                yield return Expect("e array of entities", "$piped | " + call, lastWins ? new[] { Entity(OtherId), typed } : new[] { typed, Entity(OtherId) }, typedBinds, typedElsewhere, both: !lastWins);
                yield return new Form("f empty pipeline", "@() | " + call, null, (run, calls) => NoWrite(calls).Concat(run.Errors.Select(e => "error " + e.FullyQualifiedErrorId)));
                yield return new Form("g $null", "$null | " + call, null, (run, calls) => NoWrite(calls));

                object Entity(string id)
                {
                    object entity = Activator.CreateInstance(parameter.Entity!)!;
                    // As the server sends it: every other id the object carries is 0 when it has none, never absent.
                    foreach (PropertyInfo other in parameter.Entity!.GetProperties().Where(p => p.CanWrite && p.PropertyType == typeof(ulong?) && p.Name.EndsWith("Id", StringComparison.Ordinal)))
                        other.SetValue(entity, 0UL);
                    parameter.Entity!.GetProperty(idMember)!.SetValue(entity, ulong.Parse(id, null));
                    if (nameMember is not null && nameMember != idMember && parameter.Entity.GetProperty(nameMember) is { CanWrite: true } nameProperty && nameProperty.PropertyType == typeof(string))
                        nameProperty.SetValue(entity, ServerName + "-" + id);
                    return entity;
                }
            }

            // The object a generated Set- changes (its InputObject's entity), or null for any other command.
            private static Type? ChangedBy(Type cmdlet)
            {
                for (Type? t = cmdlet.BaseType; t is not null; t = t.BaseType)
                    if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ZabbixSetCmdlet<,>)) return t.GetGenericArguments()[0];
                return null;
            }

            private bool TakenElsewhere(Func<PipelineParameter, bool> takes) => pipeline.Any(p => p != parameter && takes(p));

            private static Form Expect(string name, string script, object? input, bool bound, bool elsewhere, bool both = false)
                => new(name, script, input, (run, calls) =>
                    bound ? Reached(run, calls, both)
                    : elsewhere ? []
                    : NotBound(run, calls))
                {
                    Outcome = bound ? "bound" : elsewhere ? "another parameter's case" : "refused",
                };

            private static IEnumerable<string> Reached(Run run, (string Method, JsonElement Params, string? Authorization)[] calls, bool both)
            {
                foreach (ErrorRecord error in run.Errors) yield return "error " + error.FullyQualifiedErrorId + ": " + error.Exception.Message;
                string wire = string.Join('\n', calls.Select(c => c.Method + " " + c.Params.GetRawText()));
                if (!wire.Contains(PipedId, StringComparison.Ordinal)) yield return "the piped id never reached the wire: " + Short(wire);
                if (both && !wire.Contains(OtherId, StringComparison.Ordinal)) yield return "the second piped id never reached the wire: " + Short(wire);
            }

            private static IEnumerable<string> ResolvedByName(Run run, (string Method, JsonElement Params, string? Authorization)[] calls)
            {
                foreach (ErrorRecord error in run.Errors) yield return "error " + error.FullyQualifiedErrorId + ": " + error.Exception.Message;
                string lookup = ServerName.ToUpperInvariant();
                if (!calls.Any(c => c.Method.EndsWith(".get", StringComparison.Ordinal) && c.Params.ValueKind == JsonValueKind.Object
                        && c.Params.TryGetProperty("search", out JsonElement search) && search.GetRawText().Contains(lookup, StringComparison.Ordinal)))
                    yield return "the name was not resolved by a search: " + Short(string.Join('\n', calls.Select(c => c.Method + " " + c.Params.GetRawText())));
                if (!calls.Any(c => c.Params.GetRawText().Contains(ServerId, StringComparison.Ordinal) && !c.Params.GetRawText().Contains(lookup, StringComparison.Ordinal)))
                    yield return "the resolved id never reached the wire";
            }

            private static IEnumerable<string> NotBound(Run run, (string Method, JsonElement Params, string? Authorization)[] calls)
            {
                // Not bound: the binder says so (InputObjectNotBound), or the parameter's own transformation refuses the value
                // with its reason ("... is not a reference to a ZabbixHost: give its id, its name, or the object itself"). A
                // Get-'s hidden InputObject takes every piped object, so the binder never refuses one there: the command
                // refuses it itself under the binder's own id (ZabbixGetCmdlet.EngineInputNotBound), so a script sees one id;
                // where a Get-'s piped parameter is mandatory (Get-ZbxHostDashboard -Host), the binder bound the object to
                // InputObject and refuses the record for the mandatory parameter it left unbound (InputObjectMissingMandatory).
                if (!run.Errors.Any(e => e.FullyQualifiedErrorId.StartsWith("InputObjectNotBound", StringComparison.Ordinal)
                        || e.FullyQualifiedErrorId.StartsWith("InputObjectMissingMandatory", StringComparison.Ordinal)
                        || e.FullyQualifiedErrorId.StartsWith("ParameterArgumentTransformationError", StringComparison.Ordinal)))
                    yield return "expected InputObjectNotBound or the transformation's refusal, got " + (run.Errors.Count == 0 ? "no error" : string.Join(", ", run.Errors.Select(e => e.FullyQualifiedErrorId)));
                foreach (string write in NoWrite(calls)) yield return write;
            }

            private static IEnumerable<string> NoWrite((string Method, JsonElement Params, string? Authorization)[] calls)
                => calls.Where(c => !c.Method.EndsWith(".get", StringComparison.Ordinal) && c.Method != "apiinfo.version").Select(c => "wrote " + c.Method);

            private static string Short(string text) => text.Length > 400 ? text[..400] + "…" : text;

            // The command with what its parameter set needs besides the piped parameter: a mandatory parameter gets a
            // plain value of its type, a change runs unprompted, a New- gets its required fields through -AdditionalProperty.
            private string Call()
            {
                string set = parameter.Uses.Select(u => u.ParameterSetName).FirstOrDefault(s => s != ParameterAttribute.AllParameterSets) ?? ParameterAttribute.AllParameterSets;
                var arguments = new List<string> { command };
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.Name == parameter.Name) continue;
                    ParameterAttribute[] uses = [.. property.GetCustomAttributes<ParameterAttribute>(true)];
                    if (!uses.Any(u => u.Mandatory && (u.ParameterSetName == set || u.ParameterSetName == ParameterAttribute.AllParameterSets))) continue;
                    arguments.Add("-" + property.Name + " " + ValueOf(property.PropertyType));
                }
                if (cmdlet.SupportsShouldProcess) arguments.Add("-Confirm:$false");
                if (type.GetProperty("Force") is not null) arguments.Add("-Force");
                if (cmdlet.VerbName == VerbsCommon.New && type.GetProperty("AdditionalProperty") is not null && RequiredFields() is { Length: > 0 } required)
                    arguments.Add("-AdditionalProperty @{ " + string.Join("; ", required) + " }");
                return string.Join(' ', arguments);
            }

            private string[] RequiredFields()
            {
                ApiDescription description = ApiDescription.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema", "zabbix-api.json")));
                string noun = type.Name["New".Length..^"Command".Length];
                TypeDescription? request = description.Types.FirstOrDefault(t => t.Role == "request" && t.Method?.EndsWith(".create", StringComparison.Ordinal) == true
                    && ("Zabbix" + t.Name) == "Zabbix" + noun + "CreateRequest");
                request ??= description.Types.FirstOrDefault(t => t.Role == "request" && t.Name == noun.Replace("Zbx", string.Empty, StringComparison.Ordinal) + "CreateRequest");
                if (request is null) return [];
                return [.. request.Fields.Where(f => f.Required && f.Name != parameter.Name && !parameter.Aliases.Contains(f.Name))
                    .Select(f => f.Wire + " = " + Dummy(f.Kind))];
            }

            private static string Dummy(string kind) => kind switch
            {
                "string" => "'zbx-x'",
                "int" or "long" or "double" or "bool" => "0",
                "id" => "'1'",
                _ when kind.StartsWith("enum:", StringComparison.Ordinal) => "0",
                _ when kind.StartsWith("list:", StringComparison.Ordinal) => "@()",
                _ => "@{}",
            };

            private static string ValueOf(Type type)
            {
                Type t = Nullable.GetUnderlyingType(type) ?? type;
                if (t.IsArray) t = t.GetElementType()!;
                if (t == typeof(string)) return "'zbx-x'";
                if (t == typeof(System.Security.SecureString)) return "(ConvertTo-SecureString 'x' -AsPlainText -Force)";
                if (t.IsEnum) return "'" + Enum.GetNames(t)[0] + "'";
                if (t == typeof(ulong) || t.IsGenericType) return "'1'";
                if (t == typeof(int) || t == typeof(long)) return "1";
                if (t == typeof(Hashtable)) return "@{}";
                return "$null";
            }
        }
    }
}
