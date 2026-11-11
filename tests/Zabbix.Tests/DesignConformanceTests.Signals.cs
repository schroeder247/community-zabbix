// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using Community.Zabbix.Tests.Conformance;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix.Tests;

/// <summary>
/// The signals the design standard tells a reviewer to look for, held as rules: a hand-written platform extension says why the platform's
/// own does not serve; a constructor takes no collection its caller could change afterwards; an async iterator honours the
/// token its enumerator is given; a keyed indexer throws <see cref="KeyNotFoundException"/>; an option that is a duration
/// or a size has a range; a fire-and-forget <c>Task.Run</c> and a static <c>Regex</c> call are not in a library.
/// </summary>
public sealed partial class DesignConformanceTests
{
    private static readonly string[] PlatformExtensionBases = ["Stream", "JsonConverter", "DelegatingHandler", "HttpMessageHandler", "HttpContent", "TextWriter", "TextReader"];

    /// <summary>
    /// A class that derives from a platform extension point (a <see cref="Stream"/>, a <c>JsonConverter</c>, an HTTP
    /// handler or content) is a hand-roll of something the platform might already have; its documentation records why
    /// the platform's own is not used (a <c>because</c>, a <c>rejected</c>, a "the platform has no …").
    /// </summary>
    [Fact]
    public void Every_hand_roll_signal_carries_a_rejection()
        => AssertNone(UnreasonedExtensions(Sources.Library.Where(tree => ClientPackages.Any(package => tree.FilePath.Replace('\\', '/').Contains($"/src/{package}/", StringComparison.Ordinal)))));

    [Fact]
    public void Public_constructors_take_no_mutable_collections()
    {
        List<string> offenders = [.. Libraries.PublicTypes
            .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            .SelectMany(constructor => constructor.GetParameters().Where(parameter => IsMutableCollection(parameter.ParameterType))
                .Select(parameter => $"{constructor.DeclaringType!.FullName}..ctor({parameter.Name}: {parameter.ParameterType.Name})"))];

        AssertNone(offenders);
    }

    [Fact]
    public void Every_async_iterator_marks_its_token_for_enumerator_cancellation()
    {
        List<string> offenders = [.. Libraries.Types
            .SelectMany(type => type.GetMethods(Declared))
            .Where(method => method.GetCustomAttribute<AsyncIteratorStateMachineAttribute>() is not null
                             && method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
            .Where(method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(CancellationToken))
                             && !method.GetParameters().Any(parameter => parameter.ParameterType == typeof(CancellationToken) && parameter.GetCustomAttribute<EnumeratorCancellationAttribute>() is not null))
            .Select(Describe)];

        AssertNone(offenders);
    }

    /// <summary>
    /// A keyed indexer on a read-only map the libraries publish throws <see cref="KeyNotFoundException"/> for an absent key,
    /// as <see cref="IReadOnlyDictionary{TKey, TValue}"/> promises, read from each map the libraries hand out empty.
    /// </summary>
    [Fact]
    public async Task Public_indexer_throws_key_not_found()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)));
        services.AddZabbixClient().Configure(options => { options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"); options.ApiToken = "tok"; });
        using ScriptedApi api = new ScriptedApi().On("item.get", "[]");
        services.AddHttpClient(ZabbixHttp.GetClientName()).ConfigurePrimaryHttpMessageHandler(() => api);
        await using ServiceProvider provider = services.BuildValidated();
        await using IZabbixWatcher watcher = provider.GetRequiredService<ZabbixClient>()
            .Watch(new ZabbixWatchPlan(TimeSpan.FromMinutes(1)));
        IReadOnlyDictionary<string, ZabbixApiMethod> methods = ZabbixApiMethods.All;
        IReadOnlyDictionary<(string Host, ZabbixItemKey Key), ZabbixValue> current = watcher.Current;

        Assert.Throws<KeyNotFoundException>(() => methods["no.such.method"]);
        Assert.Throws<KeyNotFoundException>(() => current[("web-01", ZabbixItemKey.Parse("agent.ping"))]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ZabbixItemKey.Parse("agent.ping")[0]);    // a position, not a key
    }

    /// <summary>
    /// Every duration or size on a public options type of the client packages refuses a negative value when it is
    /// validated (a <see cref="RangeAttribute"/>, or the type's own <see cref="IValidatableObject"/> for a duration), as
    /// the hosting packages' options are held in <c>AdversarialOptionsTests</c>.
    /// </summary>
    [Fact]
    public void Every_duration_and_size_option_of_the_client_packages_refuses_a_negative_value()
    {
        List<string> offenders = [];
        foreach (PropertyInfo property in Libraries.All.Where(library => ClientPackages.Contains(library.GetName().Name))
            .SelectMany(library => library.GetExportedTypes())
            .Where(type => type.Name.EndsWith("Options", StringComparison.Ordinal) && type is { IsClass: true, IsAbstract: false } && type.GetConstructor(Type.EmptyTypes) is not null)
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.CanWrite && property.DeclaringType == type)))
        {
            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type != typeof(TimeSpan) && type != typeof(int) && type != typeof(long)) continue;
            object options = Activator.CreateInstance(property.DeclaringType!)!;
            property.SetValue(options, type == typeof(TimeSpan) ? TimeSpan.FromSeconds(-1) : type == typeof(int) ? -1 : (object)-1L);
            var results = new List<ValidationResult>();
            Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
            if (options is IValidatableObject validatable) results.AddRange(validatable.Validate(new ValidationContext(options)));
            if (!results.Any(result => result.MemberNames.Contains(property.Name))) offenders.Add($"{property.DeclaringType!.FullName}.{property.Name}");
        }

        AssertNone(offenders);
    }

    [Fact]
    public void No_library_discards_a_task_run_or_calls_a_static_regex()
        => AssertNone(Waivers.Outstanding(nameof(No_library_discards_a_task_run_or_calls_a_static_regex), ReviewSignals(Sources.Library)));

    [Fact]
    public void The_signal_rules_find_their_probes()
    {
        SyntaxTree tree = Sources.ParseText("""
            class A : System.IO.Stream
            {
                void M() { _ = System.Threading.Tasks.Task.Run(() => { }); bool b = System.Text.RegularExpressions.Regex.IsMatch("a", "a"); }
            }
            /// <summary>A converter, because the platform has none for this shape.</summary>
            class B : System.Text.Json.Serialization.JsonConverter<int> { }
            """, "/r/src/Zabbix.Probe/A.cs");

        Assert.Equal(["A.cs:1: A derives from Stream without saying why"], UnreasonedExtensions([tree]));
        Assert.Equal(["A.cs:3: a discarded Task.Run", "A.cs:3: a static Regex.IsMatch"], ReviewSignals([tree]));
        Assert.True(IsMutableCollection(typeof(List<string>)));
        Assert.True(IsMutableCollection(typeof(string[])));
        Assert.False(IsMutableCollection(typeof(IReadOnlyList<string>)));
        Assert.False(IsMutableCollection(typeof(string)));
    }

    private static bool IsMutableCollection(Type type)
    {
        if (type == typeof(string) || type.IsPointer) return false;
        if (type.IsArray) return true;
        if (!type.IsGenericType) return false;
        Type definition = type.GetGenericTypeDefinition();
        return definition == typeof(List<>) || definition == typeof(Dictionary<,>) || definition == typeof(HashSet<>)
               || definition == typeof(IList<>) || definition == typeof(ICollection<>) || definition == typeof(IDictionary<,>) || definition == typeof(ISet<>);
    }

    internal static IEnumerable<string> UnreasonedExtensions(IEnumerable<SyntaxTree> trees)
    {
        foreach (SyntaxTree tree in trees)
        {
            foreach (ClassDeclarationSyntax type in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                string? extended = type.BaseList?.Types.Select(baseType => SimpleName(baseType.Type)).FirstOrDefault(name => name is not null && PlatformExtensionBases.Contains(name));
                if (extended is null) continue;
                string documentation = string.Concat(type.GetLeadingTrivia().Select(trivia => trivia.ToFullString()));
                if (Reasoned(documentation)) continue;
                yield return $"{Path.GetFileName(tree.FilePath)}:{type.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {type.Identifier.ValueText} derives from {extended} without saying why";
            }
        }
    }

    private static bool Reasoned(string documentation)
        => new[] { "because", "rejected", "has no ", "have no ", "is not used", "neither ", "cannot ", "owned", "instead of", "so that", ", so ", "would " }
            .Any(word => documentation.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A usage error is thrown when the method is called, not when its task is awaited (Implementing the TAP: "usage
    /// errors" throw synchronously): a public <c>async</c> method or iterator validates nothing in its own body, and
    /// validates in a non-async public wrapper over a private async core instead, the dotnet/runtime shape.
    /// </summary>
    [Fact]
    public void Public_async_members_validate_their_arguments_before_the_state_machine()
        => AssertNone(Waivers.Outstanding(nameof(Public_async_members_validate_their_arguments_before_the_state_machine), ValidationInsideAsync(Sources.Library)));

    [Fact]
    public void The_async_validation_rule_finds_a_check_in_a_public_async_body_only()
    {
        SyntaxTree tree = Sources.ParseText("""
            public class A
            {
                public async System.Threading.Tasks.Task RunAsync(string s) { System.ArgumentNullException.ThrowIfNull(s); await System.Threading.Tasks.Task.Yield(); }
                public async System.Threading.Tasks.Task SendAsync(int n) { if (n < 0) throw new System.ArgumentOutOfRangeException(nameof(n)); await System.Threading.Tasks.Task.Yield(); }
                public System.Threading.Tasks.Task GoodAsync(string s) { System.ArgumentNullException.ThrowIfNull(s); return CoreAsync(); }
                private async System.Threading.Tasks.Task CoreAsync() { System.ArgumentNullException.ThrowIfNull(this); await System.Threading.Tasks.Task.Yield(); }
                public async System.Threading.Tasks.Task LambdaAsync() { System.Action check = () => System.ArgumentNullException.ThrowIfNull(this); await System.Threading.Tasks.Task.Yield(); }
            }
            """, "/r/src/Zabbix.Probe/A.cs");

        Assert.Equal(["A.cs: A.RunAsync", "A.cs: A.SendAsync"], ValidationInsideAsync([tree]));
    }

    internal static IEnumerable<string> ValidationInsideAsync(IEnumerable<SyntaxTree> trees)
    {
        foreach (SyntaxTree tree in trees)
        {
            foreach (MethodDeclarationSyntax method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!method.Modifiers.Any(modifier => modifier.ValueText == "async") || !method.Modifiers.Any(modifier => modifier.ValueText == "public")) continue;
                if (method.Parent is not TypeDeclarationSyntax type || !type.Modifiers.Any(modifier => modifier.ValueText == "public")) continue;
                SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                if (body is null) continue;
                bool validates = body.DescendantNodes(node => node is not (LambdaExpressionSyntax or LocalFunctionStatementSyntax or AnonymousMethodExpressionSyntax))
                    .Any(node => node switch
                    {
                        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } => access.Name.Identifier.ValueText.StartsWith("ThrowIf", StringComparison.Ordinal)
                                                                                                              && access.Expression.ToString().EndsWith("Exception", StringComparison.Ordinal),
                        ThrowStatementSyntax { Expression: ObjectCreationExpressionSyntax created } => created.Type.ToString().Split('.')[^1].StartsWith("Argument", StringComparison.Ordinal),
                        ThrowExpressionSyntax { Expression: ObjectCreationExpressionSyntax created } => created.Type.ToString().Split('.')[^1].StartsWith("Argument", StringComparison.Ordinal),
                        _ => false,
                    });
                if (validates) yield return $"{Path.GetFileName(tree.FilePath)}: {type.Identifier.ValueText}.{method.Identifier.ValueText}";
            }
        }
    }

    internal static IEnumerable<string> ReviewSignals(IEnumerable<SyntaxTree> trees)
    {
        foreach (SyntaxTree tree in trees)
        {
            foreach (InvocationExpressionSyntax call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is not MemberAccessExpressionSyntax access) continue;
                string receiver = access.Expression.ToString();
                string name = access.Name.Identifier.ValueText;
                string where = $"{Path.GetFileName(tree.FilePath)}:{call.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
                if (name == "Run" && receiver.EndsWith("Task", StringComparison.Ordinal) && call.Parent is AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.ValueText: "_" } } or ExpressionStatementSyntax)
                    yield return $"{where}: a discarded Task.Run";
                if (receiver.EndsWith("Regex", StringComparison.Ordinal) && name is "IsMatch" or "Match" or "Matches" or "Replace" or "Split" or "Count")
                    yield return $"{where}: a static Regex.{name}";
            }
        }
    }
}
