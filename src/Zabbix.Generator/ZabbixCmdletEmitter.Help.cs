// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text;
using System.Text.RegularExpressions;

namespace Community.Zabbix.Generator;

// The help of the generated cmdlets: a synopsis, a description and examples per command, written as the class's doc
// comment (<para type="synopsis">, <para type="description">, <example>) for Zabbix.PowerShell.HelpGen to read. Every
// sentence is composed from the API object's own help words (schema/overlay.json objects.<object>.help, carried on
// ServiceDescription.Help), the object's .get/.create/.update/.delete methods and the command's real parameters; an
// example names only parameters the command has, and values from the object's domain (the overlay's sample name and
// arguments), never a placeholder. A command the data cannot give a complete example for gets none, and the help build
// refuses it until a help script (Examples/<Verb-Noun>.ps1) or the overlay supplies one.
internal sealed partial class ZabbixCmdletEmitter
{
    // The objects a Get example selects by, best first: the one a reader most often scopes to.
    private static readonly string[] s_scopeObjects = ["host", "template", "hostgroup", "templategroup", "discoveryrule", "trigger", "item", "user", "usergroup", "service", "dashboard", "sla", "proxy", "mediatype", "action", "drule"];

    private static readonly Regex s_argument = new(@"(?<![\w$])-(\w+)", RegexOptions.CultureInvariant);

    private static ServiceHelp HelpOf(ServiceDescription service, EntityInfo entity)
        => service.Help ?? new ServiceHelp(Prose(entity.Noun), null, null, null, null, null, null, null, null, null, null);

    private ServiceHelp? HelpOf(string apiObject)
        => api.Services.FirstOrDefault(s => s.Object == apiObject)?.Help;

    // The object's own sentence, as plain text, ending in a period.
    private static string? SummarySentence(ServiceDescription service)
    {
        if (service.Summary is null) return null;
        string text = CodeText.Plain(service.Summary);
        return text.Length == 0 ? null : text.EndsWith(".", StringComparison.Ordinal) ? text : text + ".";
    }

    private static void EmitHelp(StringBuilder code, string synopsis, IEnumerable<string> description, IEnumerable<(string Title, string Code, string Remarks)> examples)
    {
        code.Append("/// <summary><para type=\"synopsis\">").Append(Xml(synopsis)).Append("</para>\n");
        code.Append("/// <para type=\"description\">").Append(Xml(string.Join(" ", description))).Append("</para></summary>\n");
        foreach ((string title, string example, string remarks) in examples)
        {
            code.Append("/// <example title=\"").Append(Xml(title)).Append("\">\n/// <code>\n");
            foreach (string line in example.Split('\n')) code.Append("/// ").Append(Xml(line)).Append('\n');
            code.Append("/// </code>\n/// <para>").Append(Xml(remarks)).Append("</para>\n/// </example>\n");
        }
    }

    // XML text (and a double-quoted attribute): only what XML requires, so the C# reader's doc comment stays readable.
    private static string Xml(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // "-A", "-A and -B", "-A, -B and -C".
    private static string Series(IReadOnlyList<string> items)
        => items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[items.Count - 1],
        };

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

    // ----- Get ---------------------------------------------------------------------------------------------------

    // The .get options that select by another object (a Get's -Host, -Group, -Template ...), as EmitGet declares them.
    private List<(FieldDescription Field, string Concept, EntityInfo? Target)> GetFilters(EntityInfo entity)
        => [.. entity.GetParameters.Fields
            .Where(f => f.Kind == "list:id" && f.Wire != entity.IdField + "s")
            .Select(f =>
            {
                string concept = f.Name.EndsWith("Ids", StringComparison.Ordinal) ? f.Name.Substring(0, f.Name.Length - 3) : f.Name;
                return (f, concept, TryGetFilterEntity(entity.Object, concept));
            })];

    // The parameter name a reader types for a filter: the concept (-Host), which is the property or its alias.
    private static string FilterParameter(string concept) => concept;

    private void EmitGetHelp(StringBuilder code, ServiceDescription service, EntityInfo entity, List<(FieldDescription Field, string Concept, EntityInfo? Target)> filters, bool hasSelect)
    {
        ServiceHelp help = HelpOf(service, entity);
        string command = "Get-Zbx" + entity.Noun;
        string synopsis = help.Role is null ? "Gets " + help.Nouns + "." : "Gets the " + help.Nouns + " " + help.Role + ".";

        List<(string Parameter, EntityInfo Target, ServiceHelp? TargetHelp)> scopes =
        [
            .. filters.Where(f => f.Target is not null).Select(f => (FilterParameter(f.Concept), f.Target!, HelpOf(f.Target!.Object))),
        ];
        var description = new List<string>();
        if (SummarySentence(service) is { } summary) description.Add(summary);
        description.Add("The " + command + " cmdlet reads them with " + service.Object + ".get: all of them up to -Limit"
            + (entity.NameField is null ? string.Empty : ", by name with -Name (* and ? are wildcards)")
            + ", by id with -Id or from the pipeline, or with the whole " + service.Object + ".get selection in -Filter.");
        // A filter the API requires is in every call (Get-ZbxHostDashboard -Host).
        List<(FieldDescription Field, string Concept, EntityInfo? Target)> requiredFilters = [.. filters.Where(f => f.Target is not null && f.Field.Required)];
        if (requiredFilters.Count > 0)
        {
            description.Add(Series([.. requiredFilters.Select(f => "-" + FilterParameter(f.Concept))]) + (requiredFilters.Count == 1 ? " is" : " are")
                + " required: " + service.Object + ".get reads the " + help.Nouns + " of the objects given, each a name, an id or a piped object.");
        }
        else if (scopes.Count > 0)
        {
            string[] named = [.. scopes.Take(3).Select(s => "-" + s.Parameter)];
            description.Add(scopes.Count == 1
                ? named[0] + " narrows the result to the " + help.Nouns + " of the given " + (scopes[0].TargetHelp?.Nouns ?? Prose(scopes[0].Target.Noun) + "s") + ", each a name, an id or a piped object."
                : (scopes.Count > 3 ? string.Join(", ", named) + " and the other object filters" : Series(named)) + " narrow the result to the " + help.Nouns + " of the objects given, each a name, an id or a piped object.");
        }
        description.Add("It writes " + Prefixed(entity.Read.Name) + " objects"
            + (hasSelect ? ", with their related objects when a -Select switch asks for them," : string.Empty)
            + " or, with -CountOutput, the number of matches; it needs a connection made with Connect-ZbxServer.");

        string required = string.Concat(requiredFilters
            .Where(f => HelpOf(f.Target!.Object)?.Sample is not null)
            .Select(f => " -" + FilterParameter(f.Concept) + " '" + HelpOf(f.Target!.Object)!.Sample + "'"));
        var examples = new List<(string, string, string)>();
        if (entity.NameField is not null && help.Sample is not null)
        {
            examples.Add(("Get " + help.WithArticle + " by name", command + required + " -Name '" + help.Sample + "'",
                "Gets the " + help.Noun + " named " + help.Sample + ". " + (Stem(help.Sample) is { } stem
                    ? "A * or ? in -Name makes it a search, so -Name '" + stem + "*' gets every " + help.Noun + " whose name starts with " + stem + "."
                    : "A * or ? in -Name makes it a search.")));
        }
        (string Parameter, EntityInfo Target, ServiceHelp? TargetHelp) scope = scopes
            .Where(s => s.TargetHelp?.Sample is not null && Array.IndexOf(s_scopeObjects, s.Target.Object) >= 0)
            .OrderBy(s => Array.IndexOf(s_scopeObjects, s.Target.Object))
            .FirstOrDefault();
        if (scope.TargetHelp is { Sample: { } sample } targetHelp && !filters.First(f => FilterParameter(f.Concept) == scope.Parameter).Field.Required)
        {
            examples.Add(("Get the " + help.Nouns + " of " + targetHelp.WithArticle, command + required + " -" + scope.Parameter + " '" + sample + "'",
                "Gets the " + help.Nouns + " of the " + targetHelp.Noun + " " + sample + ". -" + scope.Parameter + " takes a name, an id or an object; a "
                + targetHelp.Noun + " piped from Get-Zbx" + scope.Target.Noun + " binds to it by its " + scope.Parameter + "Id property."));
        }
        else if (requiredFilters.FirstOrDefault(f => HelpOf(f.Target!.Object)?.Sample is not null) is { Target: { } owner } first)
        {
            ServiceHelp ownerHelp = HelpOf(owner.Object)!;
            examples.Add(("Get the " + help.Nouns + " of " + ownerHelp.WithArticle, command + required,
                "Gets the " + help.Nouns + " of the " + ownerHelp.Noun + " " + ownerHelp.Sample + "; " + service.Object + ".get requires -"
                + FilterParameter(first.Concept) + ", which takes a name, an id or an object piped from Get-Zbx" + owner.Noun + "."));
        }
        // A -Filter hashtable is the whole selection, so it would have to repeat an option the API requires
        // (hostdashboard.get's hostids) that -Host gives: such a command counts instead.
        if (entity.NameField is not null && help.Sample is not null && Stem(help.Sample) is { } word && requiredFilters.Count == 0)
        {
            examples.Add(("Search " + help.Nouns + " with a filter", command + required + " -Filter @{ search = @{ " + entity.NameField + " = '" + word + "' }; limit = 10 }",
                "-Filter takes the whole " + service.Object + ".get selection, keyed by the API's field names: here at most ten " + help.Nouns + " whose " + entity.NameField + " contains " + word + "."));
        }
        else
        {
            examples.Add(("Count " + help.Nouns, command + required + " -CountOutput",
                "Writes how many " + help.Nouns + " the server has, without reading them."));
        }
        EmitHelp(code, synopsis, description, examples);
    }

    // The first word of a sample name, for a search or a wildcard (Notify on disaster -> Notify; web-01 -> web); null
    // when the name does not start with a word of three letters or digits ({$APP.PORT}).
    private static string? Stem(string sample)
    {
        string word = sample.Split(' ')[0];
        int cut = word.IndexOfAny(['-', '.', '/', '_']);
        if (cut > 0) word = word.Substring(0, cut);
        return word.Length >= 3 && word.All(char.IsLetterOrDigit) ? word : null;
    }

    // ----- New ---------------------------------------------------------------------------------------------------

    private void EmitNewHelp(StringBuilder code, ServiceDescription service, EntityInfo entity, List<(FieldDescription Field, ParamInfo Info)> parameters, HashSet<string> siblingNames)
    {
        ServiceHelp help = HelpOf(service, entity);
        string command = "New-Zbx" + entity.Noun;
        FieldDescription? owner = parameters.Select(p => p.Field).FirstOrDefault(f => f.Wire == "hostid" && parameters.Any(p => p.Field == f && p.Info.IsId && p.Info.Reference is null));
        List<string> requiredNames = [.. parameters.Where(p => IsEscapableRequired(service.Object, p.Field) && p.Field != owner).Select(p => "-" + CmdletPropertyName(service.Object, p.Field))];

        var description = new List<string>();
        if (SummarySentence(service) is { } summary) description.Add(summary);
        description.Add("The " + command + " cmdlet sends " + service.Object + ".create with one request field per parameter, then reads the new " + help.Noun + " back with " + service.Object + ".get and writes it.");
        description.Add(requiredNames.Count == 0
            ? "-AdditionalProperty sets any other field by its API name and wins over a parameter."
            : "The server requires " + Series(requiredNames) + "; -AdditionalProperty sets any other field by its API name and wins over a parameter.");
        if (owner is not null) description.Add("A host or template piped from Get-ZbxHost or Get-ZbxTemplate binds -HostId, so one command creates " + help.WithArticle + " on each.");
        description.Add("-WhatIf shows what would be created, and the command needs a connection made with Connect-ZbxServer.");

        var examples = new List<(string, string, string)>();
        if (NewCall(service, entity, help, parameters, siblingNames, owner) is { } call)
        {
            ServiceHelp? host = HelpOf("host");
            string on = owner is not null && host?.Sample is not null ? " on the host " + host.Sample : string.Empty;
            string pipe = owner is not null && host?.Sample is not null ? "Get-ZbxHost -Name '" + host.Sample + "' | " : string.Empty;
            string setup = help.CreateSetup is null ? string.Empty : help.CreateSetup + "\n";
            examples.Add(("Create " + help.WithArticle, setup + pipe + call,
                "Creates " + help.WithArticle + (help.Sample is null ? string.Empty : " named " + help.Sample) + on + ". "
                + (help.CreateNote ?? "The command writes the new " + help.Noun + ", read back from the server.")));
            // Not when the create reads an object of one host first (an item prototype's discovery rule): the other
            // hosts of the group do not have it.
            if (owner is not null && help.Sample is not null && help.CreateSetup is null)
            {
                examples.Add(("Create " + help.WithArticle + " on every host of a group", setup + "Get-ZbxHost -Group 'Linux servers' | " + call,
                    "A host piped from Get-ZbxHost binds -HostId, so every host of the group Linux servers gets its own " + help.Noun + "."));
            }
        }
        EmitHelp(code, "Creates " + help.WithArticle + ".", description, examples);
    }

    // The New call: the name, the overlay's arguments, then whatever required field they leave out that has a value a
    // rule can give (a reference: the referenced object's sample name; an enumeration: its first member). Null when a
    // required field is still missing: an example that the server would refuse is not an example.
    private string? NewCall(ServiceDescription service, EntityInfo entity, ServiceHelp help, List<(FieldDescription Field, ParamInfo Info)> parameters, HashSet<string> siblingNames, FieldDescription? owner)
    {
        var call = new StringBuilder("New-Zbx").Append(entity.Noun);
        var given = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FieldDescription? name = parameters.Select(p => p.Field).FirstOrDefault(f => f.Wire == entity.NameField && f.Kind == "string");
        if (name is not null)
        {
            if (help.Sample is null) return null;
            string parameter = CmdletPropertyName(service.Object, name);
            call.Append(" -").Append(parameter).Append(" '").Append(help.Sample).Append('\'');
            given.Add(parameter);
        }
        if (help.Create is not null)
        {
            call.Append(' ').Append(help.Create);
            foreach (Match match in s_argument.Matches(help.Create)) given.Add(match.Groups[1].Value);
        }
        foreach ((FieldDescription field, ParamInfo info) in parameters)
        {
            if (field == owner || !IsEscapableRequired(service.Object, field)) continue;
            string parameter = CmdletPropertyName(service.Object, field);
            if (given.Contains(parameter) || given.Contains(CmdletPropertyName(service.Object, field)) || given.Contains(field.Name)) continue;
            if (info.Reference is not null && HelpOf(info.Reference.EntityObject)?.Sample is { } referenced)
                call.Append(" -").Append(parameter).Append(" '").Append(referenced).Append('\'');
            else if (field.Kind.StartsWith("enum:", StringComparison.Ordinal) && _enums.TryGetValue(field.Kind.Substring(5), out EnumDescription? enumeration)
                && enumeration.Members.FirstOrDefault(m => m.Removed is null && m.Name != "None") is { } member)
                call.Append(" -").Append(parameter).Append(' ').Append(member.Name);
            else
                return null;
        }
        return call.ToString();
    }


    // ----- Set and Remove --------------------------------------------------------------------------------------------

    // The pipeline that writes the object a Set or Remove example works on, and how the remarks name it; null when the
    // object can be named directly (-InputObject 'name').
    private (string Find, string Found)? Target(ServiceDescription service, EntityInfo entity, ServiceHelp help, bool ownedByHost)
    {
        if (help.Find is not null) return (help.Find, help.Found ?? "the " + help.Noun);
        ServiceHelp? host = HelpOf("host");
        if (ownedByHost && help.Sample is not null && host?.Sample is not null && GetFilters(entity).Any(f => f.Target?.Object == "host"))
            return ("Get-Zbx" + entity.Noun + " -Host '" + host.Sample + "' -Name '" + help.Sample + "'", "the " + help.Noun + " " + help.Sample + " of " + host.Sample);
        return null;
    }

    private bool OwnedByHost(ServiceDescription service)
        => _types.Values.Any(t => t.Object == service.Object && t.Role == "request" && t.Method == service.Object + ".create" && t.Fields.Any(f => f.Wire == "hostid" && f.Kind == "id"));

    private void EmitSetHelp(StringBuilder code, ServiceDescription service, EntityInfo entity, List<(FieldDescription Field, ParamInfo Info)> parameters, HashSet<string> siblingNames)
    {
        ServiceHelp help = HelpOf(service, entity);
        string command = "Set-Zbx" + entity.Noun;
        var description = new List<string>();
        if (SummarySentence(service) is { } summary) description.Add(summary);
        description.Add("The " + command + " cmdlet changes the " + help.Nouns + " given to -InputObject (names, ids or piped objects) with " + service.Object + ".update; a field whose parameter is not given keeps its value.");
        description.Add("Every " + help.Noun + " the pipeline brings is changed in one call when the pipeline ends, and -AdditionalProperty sets any other field by its API name.");
        description.Add("The command writes nothing unless -PassThru is given, which reads the changed " + help.Nouns + " back and writes them; -WhatIf shows what would change.");

        var examples = new List<(string, string, string)>();
        (string Arguments, string Note)? change = Change(service, help, parameters, siblingNames);
        (string Find, string Found)? target = Target(service, entity, help, OwnedByHost(service));
        if (change is { } c && (target is not null || help.Sample is not null))
        {
            string named = target?.Found ?? "the " + help.Noun + " " + help.Sample;
            string call = target is { } t ? t.Find + " | " + command + " " + c.Arguments : command + " -InputObject '" + help.Sample + "' " + c.Arguments;
            examples.Add(("Change " + help.WithArticle, call, c.Note.Replace("{target}", named)));
            examples.Add(("Change " + help.WithArticle + " and write the result", call + " -PassThru",
                "The same change with -PassThru, which reads " + named + " back from the server and writes it; without it the command writes nothing."));
        }
        EmitHelp(code, "Changes " + help.WithArticle + ".", description, examples);
    }

    // The overlay's change, or one a rule can give: disable it, else describe it. Never an identity, a uuid or a name.
    private (string Arguments, string Note)? Change(ServiceDescription service, ServiceHelp help, List<(FieldDescription Field, ParamInfo Info)> parameters, HashSet<string> siblingNames)
    {
        if (help.Change is not null) return (help.Change, help.ChangeNote ?? "Changes {target}.");
        foreach ((FieldDescription field, ParamInfo _) in parameters)
        {
            if (field.Wire == "status" && field.Kind.StartsWith("enum:", StringComparison.Ordinal) && _enums.TryGetValue(field.Kind.Substring(5), out EnumDescription? enumeration)
                && enumeration.Members.FirstOrDefault(m => m.Name.StartsWith("Disabled", StringComparison.Ordinal)) is { } disabled)
                return ("-" + CmdletPropertyName(service.Object, field) + " " + disabled.Name, Capitalized("disables {target}."));
        }
        if (parameters.Any(p => p.Field.Wire == "description" && p.Field.Kind == "string" && p.Field.Wire != EntityNameWire(service)))
            return ("-Description 'Owned by the operations team'", "Sets the description of {target}.");
        return null;
    }

    private string? EntityNameWire(ServiceDescription service) => _entityByObject.TryGetValue(service.Object, out EntityInfo? entity) ? entity.NameField : null;

    private void EmitRemoveHelp(StringBuilder code, ServiceDescription service, EntityInfo entity)
    {
        ServiceHelp help = HelpOf(service, entity);
        string command = "Remove-Zbx" + entity.Noun;
        var description = new List<string>();
        if (SummarySentence(service) is { } summary) description.Add(summary);
        description.Add("The " + command + " cmdlet deletes the " + help.Nouns + " given to -InputObject (names, ids or piped objects) with " + service.Object + ".delete, all in one call when the pipeline ends.");
        description.Add("It asks for confirmation first unless -Confirm:$false or -Force is given, and -WhatIf lists what would be deleted.");
        description.Add("It writes nothing, and needs a connection made with Connect-ZbxServer.");

        var examples = new List<(string, string, string)>();
        (string Find, string Found)? target = Target(service, entity, help, OwnedByHost(service));
        if (target is not null || help.Sample is not null)
        {
            string named = target?.Found ?? "the " + help.Noun + " " + help.Sample;
            string call = target is { } t ? t.Find + " | " + command : command + " -InputObject '" + help.Sample + "'";
            examples.Add(("Delete " + help.WithArticle, call + " -Confirm:$false",
                Capitalized("deletes " + named + ". -Confirm:$false skips the prompt the command shows by default.")));
            string? stem = target is null && entity.NameField is not null && help.Sample is not null ? Stem(help.Sample) : null;
            examples.Add(stem is not null
                ? ("Preview deleting the " + help.Nouns + " a search finds", "Get-Zbx" + entity.Noun + " -Name '" + stem + "*' | " + command + " -WhatIf",
                    "Lists the " + help.Nouns + " whose names start with " + stem + " that the command would delete, and deletes nothing.")
                : ("Preview deleting " + help.WithArticle, call + " -WhatIf", "-WhatIf shows what the command would delete, and deletes nothing."));
        }
        EmitHelp(code, "Removes " + help.Nouns + ".", description, examples);
    }

    // ----- builders ------------------------------------------------------------------------------------------------

    private static void EmitBuilderHelp(StringBuilder code, string concept, string noun, string use, string what, IReadOnlyList<string> positional)
    {
        var description = new List<string>
        {
            what,
            "The New-Zbx" + concept + " cmdlet writes it as a hashtable keyed by the API's field names, which " + use + " take as it is, alone or in an array.",
        };
        if (positional.Count > 0) description.Add(Series([.. positional.Select(p => "-" + p)]) + (positional.Count == 1 ? " is" : " are") + " positional.");
        description.Add("Nothing is sent to the server, so the command needs no connection.");
        EmitHelp(code, "Builds " + ServiceHelp.Article(noun) + " " + noun + " for " + use + ".", description, []);
    }
}
