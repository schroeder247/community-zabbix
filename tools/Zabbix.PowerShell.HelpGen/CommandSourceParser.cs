// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Community.Zabbix.PowerShell.HelpGen;

/// <summary>
/// Reads <c>[Cmdlet]</c>/<c>[Alias]</c>/<c>[Parameter]</c> declarations and their XML doc comments from cmdlet source
/// (hand-written <c>Commands/*.cs</c> and the generator's own <c>obj/generated/**/*.g.cs</c>) through Roslyn's syntax
/// tree: a class is a <see cref="ClassDeclarationSyntax"/> with its own members, an attribute is an
/// <see cref="AttributeSyntax"/> with its own arguments, and a doc comment is the declaration's structured
/// <see cref="DocumentationCommentTriviaSyntax"/>, so a brace inside an attribute argument, an attribute quoted in a
/// doc comment or an undecorated class between two cmdlets cannot move a boundary. Syntax only, no compilation: the
/// assembly these files build is the one the same <c>Build</c> is writing (see the comment in
/// Zabbix.PowerShell.HelpGen.csproj), and every name read here is spelled in the source.
/// </summary>
internal static class CommandSourceParser
{
    // Preview: the modules compile with the SDK's newest language version; a construct the pinned Roslyn does not know
    // yet still parses with recovery, and the declarations read here are older than any of them.
    private static readonly CSharpParseOptions s_options = new(LanguageVersion.Preview, DocumentationMode.Parse);

    /// <summary>One class's own (not inherited) <c>[Parameter]</c>-decorated properties, and what it derives from.</summary>
    public sealed record ClassParameters(string BaseType, IReadOnlyList<ParameterDoc> Own);

    /// <summary>Parses one source file's full text.</summary>
    /// <param name="text">The file.</param>
    public static CompilationUnitSyntax Parse(string text) => CSharpSyntaxTree.ParseText(text, s_options).GetCompilationUnitRoot();

    /// <summary>Every <c>const string Name = "value"</c> field in <paramref name="source"/>: parameter set names are such constants.</summary>
    /// <param name="source">A parsed file.</param>
    public static Dictionary<string, string> Constants(CompilationUnitSyntax source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (FieldDeclarationSyntax field in source.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            if (!field.Modifiers.Any(SyntaxKind.ConstKeyword)) continue;
            foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
            {
                if (variable.Initializer?.Value is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                    constants[variable.Identifier.Text] = literal.Token.ValueText;
            }
        }
        return constants;
    }

    /// <summary>
    /// Every <c>[Cmdlet]</c> class of <paramref name="commands"/>, each with the parameters its base chain contributes
    /// after its own. Every class of both source sets (cmdlet or not) goes into the base-type graph — a module-specific
    /// intermediate base such as <c>ZabbixHostStatusCommand</c> lives in <c>Commands/*.cs</c>, right alongside the
    /// <c>[Cmdlet]</c> classes that derive it — and a parameter set name resolves against the constants of
    /// <paramref name="bases"/> (<c>ZabbixParameterSets.ByName</c>), a file's own constants winning.
    /// </summary>
    /// <param name="commands">The files declaring the commands.</param>
    /// <param name="bases">The files declaring the cmdlet base classes and the shared parameter set names.</param>
    public static IReadOnlyList<CmdletDoc> Cmdlets(IReadOnlyList<CompilationUnitSyntax> commands, IReadOnlyList<CompilationUnitSyntax> bases)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(bases);
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CompilationUnitSyntax source in bases)
            foreach ((string name, string value) in Constants(source)) constants[name] = value;
        var classes = new Dictionary<string, ClassParameters>(StringComparer.Ordinal);
        foreach (CompilationUnitSyntax source in commands.Concat(bases))
        {
            // A partial's part without a base list (a hand-written part of a generated command) never replaces the part
            // that names the base.
            foreach ((string name, ClassParameters entry) in ParseClassParameters(source, constants))
                if (entry.BaseType.Length > 0 || !classes.ContainsKey(name)) classes[name] = entry;
        }
        Dictionary<string, List<ParameterDoc>> parts = PartialParameters(commands, constants);

        return [.. commands.SelectMany(source => CmdletClasses(source, constants))
            .Select(found => parts.TryGetValue(found.Class, out List<ParameterDoc>? more) ? found.Cmdlet.WithAdditionalParameters(more) : found.Cmdlet)
            .Select(cmdlet => cmdlet.WithAdditionalParameters(InheritedParameters(cmdlet.BaseType, classes)
                .Where(inherited => cmdlet.Parameters.All(own => !string.Equals(own.Name, inherited.Name, StringComparison.Ordinal)))))];
    }

    // The parameters the parts of a partial class without [Cmdlet] declare, by class name: a hand-written part adds to
    // the generated command (Commands/Generated/NewTokenCommand.Generate.cs adds New-ZbxToken -Generate).
    private static Dictionary<string, List<ParameterDoc>> PartialParameters(IReadOnlyList<CompilationUnitSyntax> commands, IReadOnlyDictionary<string, string> constants)
    {
        var parts = new Dictionary<string, List<ParameterDoc>>(StringComparer.Ordinal);
        foreach (CompilationUnitSyntax source in commands)
        {
            Dictionary<string, string> scope = Scope(source, constants);
            foreach (ClassDeclarationSyntax type in source.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (!type.Modifiers.Any(SyntaxKind.PartialKeyword) || Attributes(type.AttributeLists, "Cmdlet").Any()) continue;
                if (!parts.TryGetValue(type.Identifier.Text, out List<ParameterDoc>? own)) parts[type.Identifier.Text] = own = [];
                own.AddRange(Parameters(type, scope));
            }
        }
        return parts;
    }

    /// <summary>Every <c>[Cmdlet]</c> class in <paramref name="source"/>, in source order, with its own parameters only.</summary>
    /// <param name="source">A parsed file.</param>
    /// <param name="constants">String constants of every source (a set name's constant resolves against its own file first).</param>
    public static IEnumerable<CmdletDoc> ParseFile(CompilationUnitSyntax source, IReadOnlyDictionary<string, string>? constants = null)
        => CmdletClasses(source, constants).Select(found => found.Cmdlet);

    private static IEnumerable<(string Class, CmdletDoc Cmdlet)> CmdletClasses(CompilationUnitSyntax source, IReadOnlyDictionary<string, string>? constants)
    {
        ArgumentNullException.ThrowIfNull(source);
        Dictionary<string, string> scope = Scope(source, constants);
        foreach (ClassDeclarationSyntax type in source.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (Attributes(type.AttributeLists, "Cmdlet").FirstOrDefault() is not { } cmdlet) continue;
            if (CommandName(cmdlet, scope) is not (string verb, string noun)) continue;
            yield return (type.Identifier.Text, Cmdlet(type, cmdlet, verb, noun, scope));
        }
    }

    /// <summary>
    /// Every class in <paramref name="source"/> — cmdlet or not — with the <c>[Parameter]</c>-decorated properties it
    /// declares directly, keyed by class name, with its own base type alongside. A cmdlet base class chain can run
    /// several classes deep and mixes framework bases (<c>ZabbixServerCmdlet.Connection</c>, the CRUD bases' own
    /// <c>-Id</c>/<c>-Filter</c>/<c>-Limit</c>/<c>-Force</c>) with module-specific intermediate ones
    /// (<c>ZabbixHostStatusCommand</c>'s <c>-InputObject</c>/<c>-Force</c>, shared by Enable-/Disable-ZbxHost); a
    /// derived <c>[Cmdlet]</c> class inherits every one of those properties without redeclaring them, so
    /// <see cref="ParseFile"/> alone never sees them — <see cref="InheritedParameters"/> walks this map instead.
    /// </summary>
    /// <param name="source">A parsed file.</param>
    /// <param name="constants">String constants of every source.</param>
    public static IEnumerable<KeyValuePair<string, ClassParameters>> ParseClassParameters(CompilationUnitSyntax source, IReadOnlyDictionary<string, string>? constants = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        Dictionary<string, string> scope = Scope(source, constants);
        foreach (ClassDeclarationSyntax type in source.DescendantNodes().OfType<ClassDeclarationSyntax>())
            yield return new(type.Identifier.Text, new ClassParameters(BaseType(type), [.. Parameters(type, scope)]));
    }

    /// <summary>Walks <paramref name="classes"/> from <paramref name="baseType"/> up through every ancestor it can
    /// find, collecting each one's own parameters — every parameter a cmdlet whose direct base is
    /// <paramref name="baseType"/> inherits. Stops at the first name not in <paramref name="classes"/> (the true
    /// root, <c>PSCmdlet</c>/<c>ZabbixCmdlet</c>, which declares none) or on a cycle.</summary>
    /// <param name="baseType">The cmdlet's direct base.</param>
    /// <param name="classes">Every class of every source (<see cref="ParseClassParameters"/>).</param>
    public static IEnumerable<ParameterDoc> InheritedParameters(string baseType, IReadOnlyDictionary<string, ClassParameters> classes)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? current = baseType;
        while (current is { Length: > 0 } && visited.Add(current) && classes.TryGetValue(current, out ClassParameters? entry))
        {
            foreach (ParameterDoc parameter in entry.Own) yield return parameter;
            current = entry.BaseType;
        }
    }

    private static Dictionary<string, string> Scope(CompilationUnitSyntax source, IReadOnlyDictionary<string, string>? constants)
    {
        Dictionary<string, string> scope = new(constants ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach ((string name, string value) in Constants(source)) scope[name] = value;
        return scope;
    }

    private static CmdletDoc Cmdlet(ClassDeclarationSyntax type, AttributeSyntax cmdlet, string verb, string noun, IReadOnlyDictionary<string, string> scope)
    {
        IReadOnlyList<XmlNodeSyntax> doc = DocComment(type);
        string synopsis = PlainText(Para(doc, "synopsis") is { } synopsisPara ? Text(synopsisPara.Content) : string.Empty);
        string description = PlainText(Para(doc, "description") is { } descriptionPara ? Text(descriptionPara.Content) : string.Empty);
        if (description.Length == 0) description = synopsis;
        return new CmdletDoc
        {
            Verb = verb,
            Noun = noun,
            Synopsis = synopsis.Length == 0 ? $"{verb} {noun}." : synopsis,
            Description = description.Length == 0 ? $"{verb} {noun}." : description,
            BaseType = BaseType(type),
            DefaultParameterSet = Named(cmdlet, "DefaultParameterSetName") is { } set ? StringValue(set, scope) : string.Empty,
            SupportsShouldProcess = Named(cmdlet, "SupportsShouldProcess") is { } should && IsTrue(should),
            SupportsPaging = Named(cmdlet, "SupportsPaging") is { } paging && IsTrue(paging),
            Aliases = [.. Attributes(type.AttributeLists, "Alias").SelectMany(Strings)],
            // OutputType names a CLR type (typeof) or an ETS type name (a string: Zabbix.PowerShell.ServerInfo); a named
            // argument (ParameterSetName = ...) names neither.
            OutputTypes = [.. Attributes(type.AttributeLists, "OutputType")
                .SelectMany(output => output.ArgumentList?.Arguments.Where(argument => argument.NameEquals is null) ?? [])
                .Select(argument => argument.Expression switch
                {
                    TypeOfExpressionSyntax typeOf => ParameterDoc.DisplayTypeOf(typeOf.Type.ToString()),
                    LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } name => name.Token.ValueText,
                    _ => null,
                })
                .OfType<string>().Distinct(StringComparer.Ordinal)],
            Parameters = [.. Parameters(type, scope)],
            Examples = [.. Examples(doc)],
        };
    }

    // <example title="..."><code>...</code><para>remarks</para></example>, as the generator writes them on a generated
    // cmdlet: the code keeps its lines, the rest is the remarks, one paragraph per <para>.
    private static IEnumerable<ExampleDoc> Examples(IReadOnlyList<XmlNodeSyntax> doc)
    {
        foreach (XmlElementSyntax example in doc.OfType<XmlElementSyntax>().Where(element => string.Equals(element.StartTag.Name.LocalName.Text, "example", StringComparison.Ordinal)))
        {
            string title = example.StartTag.Attributes.OfType<XmlTextAttributeSyntax>()
                .Where(attribute => string.Equals(attribute.Name.LocalName.Text, "title", StringComparison.Ordinal))
                .Select(attribute => System.Net.WebUtility.HtmlDecode(string.Concat(attribute.TextTokens.Select(token => token.Text))))
                .FirstOrDefault() ?? string.Empty;
            XmlElementSyntax? code = Element(example.Content, "code");
            if (code is null) continue;
            IEnumerable<string> paragraphs = example.Content.OfType<XmlElementSyntax>()
                .Where(element => string.Equals(element.StartTag.Name.LocalName.Text, "para", StringComparison.Ordinal))
                .Select(para => PlainText(Text(para.Content)))
                .Where(text => text.Length > 0);
            yield return new ExampleDoc(title, CodeText(code), string.Join('\n', paragraphs));
        }
    }

    // A <code> element's text as written: its lines, each without the one space after "///", entities decoded.
    private static string CodeText(XmlElementSyntax code)
    {
        IEnumerable<string> lines = Text(code.Content).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => line.StartsWith(' ') ? line[1..] : line);
        return System.Net.WebUtility.HtmlDecode(string.Join('\n', lines).Trim('\n', ' '));
    }

    // [Cmdlet(VerbsCommon.Get, "ZbxHost")] or [Cmdlet("Get", "ZbxHost")]: PowerShell's Verbs* constants are named
    // identically to their value (VerbsCommon.Get is "Get"), so the member's own name is the verb without loading
    // System.Management.Automation.
    private static (string Verb, string Noun)? CommandName(AttributeSyntax cmdlet, IReadOnlyDictionary<string, string> scope)
    {
        if (cmdlet.ArgumentList is not { Arguments: [{ NameEquals: null } verb, { NameEquals: null } noun, ..] }) return null;
        string verbText = verb.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
            _ => string.Empty,
        };
        string nounText = StringValue(noun.Expression, scope);
        return verbText.Length == 0 || nounText.Length == 0 ? null : (verbText, nounText);
    }

    private static string BaseType(TypeDeclarationSyntax type)
        => type.BaseList?.Types.FirstOrDefault()?.Type is { } baseType ? Identifier(baseType) : string.Empty;

    // One ParameterDoc per public property, however many [Parameter] attributes it carries: a property in several
    // parameter sets has one per set (the Get bases' -Limit is in both All and ByName), and is documented once.
    private static IEnumerable<ParameterDoc> Parameters(TypeDeclarationSyntax type, IReadOnlyDictionary<string, string> scope)
    {
        foreach (PropertyDeclarationSyntax property in type.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (!property.Modifiers.Any(SyntaxKind.PublicKeyword)) continue;
            List<ParameterUse> uses = [.. Attributes(property.AttributeLists, "Parameter").Select(parameter => Use(parameter, scope))];
            if (uses.Count == 0) continue;
            // The <summary> is the user's text; <remarks> and anything else in the doc comment is the maintainer's.
            IReadOnlyList<XmlNodeSyntax> doc = DocComment(property);
            string description = PlainText(Element(doc, "summary") is { } summary ? Text(summary.Content) : Text(doc));
            string name = property.Identifier.Text;
            yield return new ParameterDoc
            {
                Name = name,
                TypeText = property.Type.ToString(),
                Description = description.Length == 0 ? name : description,
                Mandatory = uses.Any(u => u.Mandatory),
                ParameterSet = uses.Select(u => u.Set).FirstOrDefault(set => set.Length > 0) ?? string.Empty,
                Aliases = [.. Attributes(property.AttributeLists, "Alias").SelectMany(Strings)],
                Uses = uses,
                Wildcards = Attributes(property.AttributeLists, "SupportsWildcards").Any(),
                DefaultValue = property.Initializer is { } initializer ? DefaultValue(initializer.Value) : null,
            };
        }
    }

    /// <summary>
    /// A property initializer as the help's default value: a literal is its value, a list its values, a duration
    /// (<c>TimeSpan.FromSeconds(60)</c>) its seconds or minutes, a switch that starts on <c>True</c>, an enum member its
    /// name, and <c>Environment.MachineName</c> the PowerShell expression it is. <see langword="null"/> where the
    /// initializer says nothing a reader can use: none (<c>null!</c>, an empty string or list), or a constant declared
    /// outside the module's sources (<c>ZabbixServerNode.DefaultPort</c>), whose value the summary states instead.
    /// </summary>
    /// <param name="value">The initializer's expression.</param>
    internal static string? DefaultValue(ExpressionSyntax value)
    {
        switch (value)
        {
            case PostfixUnaryExpressionSyntax { Operand: var operand } when value.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                return DefaultValue(operand);
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NullLiteralExpression):
                return null;
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return literal.Token.ValueText.Length == 0 ? null : literal.Token.ValueText;
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression):
                return literal.IsKind(SyntaxKind.TrueLiteralExpression) ? "True" : "False";
            case LiteralExpressionSyntax literal:
                return literal.Token.ValueText;
            case PrefixUnaryExpressionSyntax { OperatorToken.Text: "-", Operand: LiteralExpressionSyntax number }:
                return "-" + number.Token.ValueText;
            case CollectionExpressionSyntax collection:
                string?[] elements = [.. collection.Elements.Select(element => element is ExpressionElementSyntax item ? DefaultValue(item.Expression) : null)];
                return elements.Length == 0 || elements.Any(element => element is null) ? null : string.Join(", ", elements);
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: var type, Name.Identifier.Text: var from }, ArgumentList.Arguments: [var argument] }
                when type is NameSyntax typeName && Identifier(typeName) == "TimeSpan" && argument.Expression is LiteralExpressionSyntax amount:
                return from switch
                {
                    "FromSeconds" => amount.Token.ValueText + " seconds",
                    "FromMinutes" => amount.Token.ValueText + " minutes",
                    "FromHours" => amount.Token.ValueText + " hours",
                    _ => null,
                };
            case ObjectCreationExpressionSyntax { ArgumentList.Arguments: [var argument] } creation when Identifier(creation.Type) == "SwitchParameter":
                return argument.Expression.IsKind(SyntaxKind.TrueLiteralExpression) ? "True" : null;
            case MemberAccessExpressionSyntax { Expression: var owner, Name.Identifier.Text: var member }:
                if (owner is NameSyntax ownerName && Identifier(ownerName) == "Environment") return "[Environment]::" + member;
                // An enum member reads as its name (Json, Unsigned); a constant (DefaultPort) has no value here.
                return member.StartsWith("Default", StringComparison.Ordinal) || member == "Empty" ? null : member;
            default:
                return null;
        }
    }

    private static ParameterUse Use(AttributeSyntax parameter, IReadOnlyDictionary<string, string> scope) => new(
        Named(parameter, "ParameterSetName") is { } set ? StringValue(set, scope) : string.Empty,
        Named(parameter, "Mandatory") is { } mandatory && IsTrue(mandatory),
        Named(parameter, "Position") is LiteralExpressionSyntax { Token.Value: int position } ? position : null,
        Named(parameter, "ValueFromPipeline") is { } byValue && IsTrue(byValue),
        Named(parameter, "ValueFromPipelineByPropertyName") is { } byName && IsTrue(byName));

    /// <summary>The attributes in <paramref name="lists"/> named <paramref name="name"/>, however qualified, with or without the <c>Attribute</c> suffix.</summary>
    private static IEnumerable<AttributeSyntax> Attributes(SyntaxList<AttributeListSyntax> lists, string name)
        => lists.SelectMany(list => list.Attributes).Where(attribute =>
        {
            string identifier = Identifier(attribute.Name);
            return string.Equals(identifier, name, StringComparison.Ordinal) || string.Equals(identifier, name + "Attribute", StringComparison.Ordinal);
        });

    private static ExpressionSyntax? Named(AttributeSyntax attribute, string name)
        => attribute.ArgumentList?.Arguments.FirstOrDefault(argument => string.Equals(argument.NameEquals?.Name.Identifier.Text, name, StringComparison.Ordinal))?.Expression;

    private static bool IsTrue(ExpressionSyntax expression) => expression.IsKind(SyntaxKind.TrueLiteralExpression);

    // Every string literal an attribute is given, in order ([Alias("a", "b")]).
    private static IEnumerable<string> Strings(AttributeSyntax attribute)
        => attribute.ArgumentList?.Arguments.SelectMany(argument => argument.Expression.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>())
            .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression)).Select(literal => literal.Token.ValueText) ?? [];

    // A literal is its value; a constant (ByName, ZabbixParameterSets.ByName) or a nameof(...) resolves by its own
    // name against the constants in scope, and is that name when none declares it.
    private static string StringValue(ExpressionSyntax expression, IReadOnlyDictionary<string, string> scope)
    {
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)) return literal.Token.ValueText;
        if (expression is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" }, ArgumentList.Arguments: [var argument] })
            return argument.Expression is NameSyntax named ? Identifier(named) : argument.Expression.ToString();
        string reference = expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
            NameSyntax name => Identifier(name),
            _ => expression.ToString(),
        };
        return scope.TryGetValue(reference, out string? value) ? value : reference;
    }

    /// <summary>A type or name's own identifier: no namespace, no <c>global::</c>, no generic arguments.</summary>
    private static string Identifier(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => Identifier(qualified.Right),
        AliasQualifiedNameSyntax alias => Identifier(alias.Name),
        SimpleNameSyntax simple => simple.Identifier.Text,
        _ => type.ToString(),
    };

    // A declaration's doc comment is structured trivia ahead of its first token (its first attribute list's '[', or
    // its first modifier): the compiler's own reading, so only a comment that documents the declaration is read.
    private static IReadOnlyList<XmlNodeSyntax> DocComment(MemberDeclarationSyntax declaration)
        => [.. declaration.GetLeadingTrivia().Select(trivia => trivia.GetStructure()).OfType<DocumentationCommentTriviaSyntax>().SelectMany(doc => doc.Content)];

    private static XmlElementSyntax? Element(IEnumerable<XmlNodeSyntax> doc, string name)
        => doc.SelectMany(node => node.DescendantNodesAndSelf().OfType<XmlElementSyntax>())
            .FirstOrDefault(element => string.Equals(element.StartTag.Name.LocalName.Text, name, StringComparison.Ordinal));

    // The first <para type="..."> anywhere in the doc comment (the cmdlets put them inside <summary>).
    private static XmlElementSyntax? Para(IEnumerable<XmlNodeSyntax> doc, string type)
        => doc.SelectMany(node => node.DescendantNodesAndSelf().OfType<XmlElementSyntax>())
            .FirstOrDefault(element => string.Equals(element.StartTag.Name.LocalName.Text, "para", StringComparison.Ordinal)
                && element.StartTag.Attributes.OfType<XmlTextAttributeSyntax>().Any(attribute =>
                    string.Equals(attribute.Name.LocalName.Text, "type", StringComparison.Ordinal)
                    && string.Equals(string.Concat(attribute.TextTokens.Select(token => token.ValueText)), type, StringComparison.Ordinal)));

    // Doc-comment XML as written, minus each line's "///" exterior: the markup PlainText reduces to prose.
    private static string Text(IEnumerable<XmlNodeSyntax> nodes)
    {
        var text = new StringBuilder();
        foreach (SyntaxToken token in nodes.SelectMany(node => node.DescendantTokens()))
        {
            AppendTrivia(text, token.LeadingTrivia);
            text.Append(token.Text);
            AppendTrivia(text, token.TrailingTrivia);
        }
        return text.ToString();
    }

    private static void AppendTrivia(StringBuilder text, SyntaxTriviaList trivia)
    {
        foreach (SyntaxTrivia item in trivia)
        {
            if (!item.IsKind(SyntaxKind.DocumentationCommentExteriorTrivia)) text.Append(item.ToFullString());
        }
    }

    /// <summary>Strips XML doc markup down to plain, human-readable text: <c>&lt;c&gt;</c>/<c>&lt;see cref&gt;</c> keep their
    /// content or referenced name, every other tag is dropped, and runs of whitespace collapse to single spaces.</summary>
    /// <param name="xmlDocFragment">Doc-comment XML, from a source doc comment or a compiler documentation file.</param>
    internal static string PlainText(string xmlDocFragment)
    {
        string text = Regex.Replace(xmlDocFragment, @"<see\s+cref=""(?:[^""]*[.:])?(\w+)(?:\{[^""]*\}|\([^""]*\))?""\s*/>", "$1");
        text = Regex.Replace(text, @"<see\s+langword=""(\w+)""\s*/>", "$$$1");
        text = Regex.Replace(text, @"<(?:paramref|typeparamref)\s+name=""(\w+)""\s*/>", "$1");
        text = Regex.Replace(text, @"<c>(.*?)</c>", "$1", RegexOptions.Singleline);
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }
}
