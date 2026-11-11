// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using Community.Zabbix.Tests.Conformance;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Community.Zabbix.Tests;

/// <summary>
/// Exception messages (Framework Design Guidelines, "Exception Throwing"): a message is a grammatically correct sentence
/// that starts with a capital and ends with a period, because it is read where the exception surfaces (a log, a shell's
/// error record, a span event), next to other sentences. A message may end with the peer's own words or an inner
/// failure's message (an interpolation hole last), which carry their own punctuation. A message never carries a value
/// classified personal, secret or item key: a message reaches every log and span unredacted.
/// </summary>
public sealed partial class DesignConformanceTests
{
    private static readonly Lazy<ILookup<string, Type>> ExceptionTypesByName = new(() =>
        Libraries.Types.Concat(typeof(object).Assembly.GetTypes()).Concat(typeof(System.Text.Json.JsonException).Assembly.GetTypes())
            .Concat(typeof(System.Security.Authentication.AuthenticationException).Assembly.GetTypes())
            .Where(type => typeof(Exception).IsAssignableFrom(type) && type.IsPublic || (type.IsNotPublic && Libraries.Contains(type.Assembly) && typeof(Exception).IsAssignableFrom(type)))
            .ToLookup(type => type.Name, StringComparer.Ordinal));

    [Fact]
    public void Exception_messages_are_sentences()
        => AssertNone(Waivers.Outstanding(nameof(Exception_messages_are_sentences), FragmentMessages(Sources.Library)));

    [Fact]
    public void The_message_rule_finds_fragments_and_spares_sentences_and_a_peer_s_words()
    {
        SyntaxTree tree = Sources.ParseText("""
            class A
            {
                void M(string info, int n)
                {
                    throw new System.InvalidOperationException("the thing failed");
                    throw new System.ArgumentException($"{n} is not positive", nameof(n));
                    throw new System.ArgumentOutOfRangeException(nameof(n), n, "not a count");
                    throw new System.InvalidOperationException("The thing failed.");
                    throw new System.InvalidOperationException($"The server refused: {info}");
                    throw new System.IO.InvalidDataException("The reply is not JSON: " + info);
                }
                static System.IO.InvalidDataException Invalid(string why) => new($"the reply is wrong: {why}");
            }
            """, "src/X/A.cs");

        Assert.Equal(["A.cs:5", "A.cs:6", "A.cs:7", "A.cs:12"], FragmentMessages([tree]).Select(offender => offender[..offender.IndexOf(' ', StringComparison.Ordinal)].TrimEnd(':')));
    }

    [Fact]
    public void No_exception_message_carries_a_personal_or_secret_value()
        => AssertNone(ClassifiedValuesInMessages(Sources.Library));

    [Fact]
    public void The_classified_message_rule_finds_a_user_name_in_a_message()
    {
        SyntaxTree tree = Sources.ParseText("""
            class A
            {
                [Community.Zabbix.ZabbixPersonal] public string UserName { get; } = "";
                void M() => throw new System.Security.Authentication.AuthenticationException($"User '{UserName}' must use MFA.");
            }
            """, "src/X/A.cs");

        Assert.Equal(["A.cs:4: UserName"], ClassifiedValuesInMessages([tree]));
    }

    [Fact]
    public void The_classified_message_rule_finds_an_item_key_parameter_in_its_own_member_only()
    {
        SyntaxTree tree = Sources.ParseText("""
            class A
            {
                static void Parse([Community.Zabbix.ZabbixItemKeyData] string s) => throw new System.FormatException($"'{s}' is not a key.");
                static void Other(string s) => throw new System.FormatException($"'{s}' is not a number.");
            }
            """, "src/X/A.cs");

        Assert.Equal(["A.cs:3: s"], ClassifiedValuesInMessages([tree]));
    }

    /// <summary>Every message argument of an exception the libraries construct that is not a sentence: <c>file:line: text</c>.</summary>
    internal static IEnumerable<string> FragmentMessages(IEnumerable<SyntaxTree> trees)
    {
        foreach ((SyntaxTree tree, ExpressionSyntax message) in Messages(trees))
        {
            if (IsSentence(message)) continue;
            yield return $"{Where(tree, message)}: {message}";
        }
    }

    internal static IEnumerable<string> ClassifiedValuesInMessages(IEnumerable<SyntaxTree> trees)
    {
        List<SyntaxTree> all = [.. trees];
        HashSet<string> classified = [.. all.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(member => member.AttributeLists.SelectMany(list => list.Attributes).Any(IsPersonalOrSecret))
            .Select(member => member switch
            {
                PropertyDeclarationSyntax property => property.Identifier.ValueText,
                FieldDeclarationSyntax field => field.Declaration.Variables[0].Identifier.ValueText,
                _ => null,
            })
            .OfType<string>())];
        foreach ((SyntaxTree tree, ExpressionSyntax message) in Messages(all))
        {
            // A classified parameter is classified in its own member only: its name means nothing elsewhere.
            HashSet<string> parameters = [.. message.Ancestors().OfType<BaseMethodDeclarationSyntax>().Take(1)
                .SelectMany(member => member.ParameterList.Parameters)
                .Where(parameter => parameter.AttributeLists.SelectMany(list => list.Attributes).Any(IsPersonalOrSecret))
                .Select(parameter => parameter.Identifier.ValueText)];
            foreach (IdentifierNameSyntax name in message.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Where(name => classified.Contains(name.Identifier.ValueText) || parameters.Contains(name.Identifier.ValueText)))
                yield return $"{Where(tree, message)}: {name.Identifier.ValueText}";
        }
    }

    private static bool IsPersonalOrSecret(AttributeSyntax attribute)
        => (attribute.Name switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            IdentifierNameSyntax simple => simple.Identifier.ValueText,
            _ => string.Empty,
        }) is "ZabbixPersonal" or "ZabbixPersonalAttribute" or "ZabbixSecret" or "ZabbixSecretAttribute" or "ZabbixItemKeyData" or "ZabbixItemKeyDataAttribute";

    private static string Where(SyntaxTree tree, SyntaxNode node)
    {
        string path = tree.FilePath.Replace('\\', '/');
        int src = path.LastIndexOf("/src/", StringComparison.Ordinal);
        string shown = src >= 0 ? path[(src + 5)..] : Path.GetFileName(path);
        return $"{shown}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
    }

    /// <summary>The message argument of every exception construction: explicit, or target-typed in a member that returns an exception.</summary>
    private static IEnumerable<(SyntaxTree Tree, ExpressionSyntax Message)> Messages(IEnumerable<SyntaxTree> trees)
    {
        foreach (SyntaxTree tree in trees)
        {
            foreach (BaseObjectCreationExpressionSyntax creation in tree.GetRoot().DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
            {
                string? type = creation switch
                {
                    ObjectCreationExpressionSyntax explicitType => SimpleName(explicitType.Type),
                    ImplicitObjectCreationExpressionSyntax => ReturnTypeOf(creation),
                    _ => null,
                };
                if (type is null || !type.EndsWith("Exception", StringComparison.Ordinal) || creation.ArgumentList is not { } arguments) continue;
                if (MessageArgument(type, arguments) is { } message) yield return (tree, message);
            }
        }
    }

    private static string? SimpleName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => null,
    };

    private static string? ReturnTypeOf(SyntaxNode node)
    {
        foreach (SyntaxNode ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax method: return SimpleName(method.ReturnType);
                case LocalFunctionStatementSyntax local: return SimpleName(local.ReturnType);
                case PropertyDeclarationSyntax property: return SimpleName(property.Type);
                case AnonymousFunctionExpressionSyntax: return null;
            }
        }
        return null;
    }

    // The argument bound to a constructor parameter named "message", found by reflection over the exception types the
    // libraries and the BCL declare with that simple name and that many arguments; a domain constructor whose first text
    // is not a message (ZabbixNotFoundException(objectType, name)) is not read as one.
    private static ExpressionSyntax? MessageArgument(string type, ArgumentListSyntax arguments)
    {
        if (arguments.Arguments.FirstOrDefault(argument => argument.NameColon?.Name.Identifier.ValueText == "message") is { } named) return named.Expression;
        int count = arguments.Arguments.Count;
        HashSet<int> positions = [.. ExceptionTypesByName.Value[type]
            .SelectMany(candidate => candidate.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .Select(constructor => constructor.GetParameters())
            .Where(parameters => !parameters.Any(parameter => parameter.ParameterType == typeof(System.Runtime.Serialization.SerializationInfo)))
            .Where(parameters => parameters.Length >= count && parameters.Skip(count).All(parameter => parameter.IsOptional))
            .Select(parameters => System.Array.FindIndex(parameters, parameter => parameter.Name == "message" && parameter.ParameterType == typeof(string)))];
        // Constructors of this arity that disagree on where the message is (or whether there is one) cannot be told apart
        // without binding; the rule reads only what it can place.
        return positions.Count == 1 && positions.Single() is >= 0 and var position && position < count ? arguments.Arguments[position].Expression : null;
    }

    private static bool IsSentence(ExpressionSyntax message)
    {
        switch (message)
        {
            case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal:
                return StartsAsSentence(literal.Token.ValueText) && EndsAsSentence(literal.Token.ValueText);
            case InterpolatedStringExpressionSyntax interpolated:
                {
                    InterpolatedStringContentSyntax? first = interpolated.Contents.FirstOrDefault();
                    InterpolatedStringContentSyntax? last = interpolated.Contents.LastOrDefault();
                    bool starts = first is not InterpolatedStringTextSyntax text || StartsAsSentence(text.TextToken.ValueText);
                    bool ends = last is not InterpolatedStringTextSyntax tail || EndsAsSentence(tail.TextToken.ValueText);
                    return starts && ends;
                }
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } concatenation:
                return concatenation.DescendantNodesAndSelf().OfType<ExpressionSyntax>().FirstOrDefault(part => part is LiteralExpressionSyntax or InterpolatedStringExpressionSyntax) is not { } head
                       || head is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } leading
                       || StartsAsSentence(leading.Token.ValueText);
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Create" } } create
                when create.ArgumentList.Arguments.LastOrDefault()?.Expression is InterpolatedStringExpressionSyntax inner:
                return IsSentence(inner);
            default:
                return true;                                                                       // built elsewhere: its own rule
        }
    }

    private static bool StartsAsSentence(string text) => text.Length == 0 || !char.IsLower(text[0]);

    private static bool EndsAsSentence(string text) => text.TrimEnd().EndsWith('.') || text.TrimEnd().EndsWith('?') || text.TrimEnd().EndsWith('!');
}
