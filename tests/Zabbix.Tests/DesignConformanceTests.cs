// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Tests.Conformance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>
/// The suite validates the design against the design standard (docs/design/decisions.md): one test per rule, named
/// as the sentence it enforces, failing with the members that break it. The assemblies are computed
/// (<see cref="Libraries"/>), never listed, and no rule exempts a member by name: an exemption is computed from a
/// signature or an attribute, and says which. A rule the build already makes an error is not repeated here, only proven
/// to be active (the probe build, <c>DesignConformanceTests.Enforcement.cs</c>): CA1715 prefixes interfaces and type
/// parameters, CS8425 marks async-iterator cancellation, IDE1006 suffixes <c>async</c> methods, CA1002/CA1819 the direct
/// list and array exposures, RS0030 the wall clock and sync-over-async. What needs the running program (a validated
/// container), the whole-assembly view (event ids, call sites) or a nested signature is here.
/// </summary>
public sealed partial class DesignConformanceTests
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly string[] ForbiddenSuffixes = ["Manager", "Helper", "Coordinator", "Orchestrator", "Engine", "Util", "Utils", "Impl"];

    [Fact]
    public void The_libraries_under_conformance_are_every_project_the_tests_reference()
    {
        List<string> names = [.. Libraries.All.Select(library => library.GetName().Name!)];

        Assert.Contains(typeof(ZabbixTelemetryNames).Assembly.GetName().Name, names);
        Assert.Contains(typeof(Zabbix.Listening.ZabbixListener).Assembly.GetName().Name, names);
        Assert.Contains(typeof(ZabbixHost).Assembly.GetName().Name, names);
        Assert.Contains(typeof(Zabbix.Storage.EntityFrameworkCore.ZabbixEntityFrameworkStore<>).Assembly.GetName().Name, names);
        Assert.Contains("Community.Zabbix.Api", names);
        Assert.Contains("Community.Zabbix.Wire", names);
        Assert.Contains("Community.Zabbix.Agent.Storage.EntityFrameworkCore", names);
        Assert.DoesNotContain("Zabbix.Generator", names);                                  // a Roslyn component
        Assert.All(names, name => Assert.StartsWith("Community.Zabbix.", name, StringComparison.Ordinal));   // each named after its package
        Assert.Equal("Community.Zabbix", Libraries.RootNamespace);
    }

    [Fact]
    public void No_type_name_has_a_forbidden_suffix()
    {
        List<string> offenders = [.. Libraries.PublicTypes
            .Where(type => ForbiddenSuffixes.Any(suffix => type.Name.EndsWith(suffix, StringComparison.Ordinal)))
            .Select(type => type.FullName!)];

        AssertNone(offenders);
    }

    /// <summary>
    /// IDE1006 suffixes a method that has the <c>async</c> modifier; one that returns a task without it (an
    /// expression body forwarding a call) escapes the analyzer. This rule reads the return type instead. Exempt, computed:
    /// an implementation or override of a contract declared outside the libraries (its name is that contract's) and an
    /// assembly's entry point.
    /// </summary>
    [Fact]
    public void Every_task_returning_method_name_ends_in_async()
        => AssertNone(UnsuffixedTaskMethods(Libraries.Types));

    [Fact]
    public void The_async_suffix_rule_finds_a_task_returning_method_without_async()
        => Assert.Equal(["AsyncSuffixProbe.Poll()"], UnsuffixedTaskMethods([typeof(AsyncSuffixProbe)]));

    [Fact]
    public void Logger_message_event_ids_are_unique_across_the_assemblies()
    {
        List<(MethodInfo Method, LoggerMessageAttribute Attribute)> messages = [.. LoggerMessages()];
        Assert.NotEmpty(messages);

        List<string> duplicates = [.. messages
            .GroupBy(entry => entry.Attribute.EventId)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(entry => Describe(entry.Method)))}")];

        AssertNone(duplicates);
    }

    /// <summary>
    /// A catalogue is not logging. Every <c>[LoggerMessage]</c> the SDK declares must be called from library
    /// code: an event defined and never emitted is a boundary that is silent while looking covered. Read from the IL call
    /// sites (<see cref="CallGraph"/>), so a comment or a string that happens to spell the name cannot satisfy it.
    /// </summary>
    [Fact]
    public void Every_catalogued_event_is_emitted_somewhere()
    {
        List<string> silent = [.. LoggerMessages()
            .Where(entry => !CallGraph.Of.Callers(entry.Method).Any(caller => caller != entry.Method))
            .Select(entry => $"{entry.Attribute.EventId} {Describe(entry.Method)}")];

        AssertNone(silent);
    }

    [Fact]
    public void Public_types_declare_no_events()
    {
        List<string> offenders = [.. Libraries.PublicTypes
            .SelectMany(type => type.GetEvents())
            .Select(@event => $"{@event.DeclaringType!.FullName}.{@event.Name}")];

        AssertNone(offenders);
    }

    /// <summary>
    /// A public constructor takes values, not the options pattern or the container: a hand-composed host is given its
    /// options and its collaborators, and the registration's factory is where a named value is read and a service resolved.
    /// </summary>
    [Fact]
    public void Public_constructors_take_no_options_monitor_and_no_service_provider()
    {
        List<string> offenders = [.. Libraries.PublicTypes
            .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            .SelectMany(constructor => constructor.GetParameters().Select(parameter => (constructor, parameter.ParameterType)))
            .Where(taken => taken.ParameterType == typeof(IServiceProvider)
                || taken.ParameterType.IsGenericType && OptionsPattern.Contains(taken.ParameterType.GetGenericTypeDefinition()))
            .Select(taken => $"{taken.constructor.DeclaringType!.FullName}({taken.ParameterType.Name})")];

        AssertNone(offenders);
    }

    /// <summary>
    /// One word and one unit per bound across the options types: a count of held records is <c>MaxItems</c>, never a
    /// <c>Capacity</c>, and a byte bound says <c>Bytes</c> (<c>MaxPayloadBytes</c>, <c>MaxBatchBytes</c>).
    /// </summary>
    [Fact]
    public void Options_name_a_bound_with_one_word_and_its_unit()
    {
        List<string> offenders = [.. OptionsTypeSet()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(property => property.Name.EndsWith("Capacity", StringComparison.Ordinal) || property.Name.EndsWith("Payload", StringComparison.Ordinal))
            .Select(property => $"{property.DeclaringType!.FullName}.{property.Name}")
            .Distinct()];

        AssertNone(offenders);
    }

    /// <summary>
    /// No static mutable state. A static field that is not <c>readonly</c> (an auto-property's backing field
    /// included: a static <c>{ get; set; }</c> is one), a static property with a setter, and a <c>static readonly</c>
    /// field whose value is itself mutable (a non-empty array, a collection that is not read-only). The compiler's own
    /// caches (lambda and delegate caches in <c>[CompilerGenerated]</c> types) are the compiler's, not state.
    /// </summary>
    [Fact]
    public void No_static_mutable_state()
        => AssertNone(Waivers.Outstanding(nameof(No_static_mutable_state), StaticMutableState(Libraries.Types)));

    [Fact]
    public void The_static_state_rule_sees_a_static_array_and_a_static_setter()
        => Assert.Equal(["Community.Zabbix.Tests.StaticStateProbe.<Current>k__BackingField", "Community.Zabbix.Tests.StaticStateProbe.Codes", "Community.Zabbix.Tests.StaticStateProbe.Current { set; }"], StaticMutableState([typeof(StaticStateProbe)]).Order(StringComparer.Ordinal));

    /// <summary>Every public <c>Add*</c> extension over <see cref="IServiceCollection"/>, by its signature.</summary>
    public static TheoryData<string> AddExtensions => new(Verbs().Select(Describe));

    [Theory]
    [MemberData(nameof(AddExtensions))]
    public void Every_add_extension_builds_a_validated_container(string verb)
    {
        MethodInfo method = Verbs().Single(candidate => Describe(candidate) == verb);
        var services = new ServiceCollection();

        method.Invoke(null, [services, .. method.GetParameters().Skip(1).Select(Synthesize)]);
        using ServiceProvider provider = services.BuildValidated();   // ValidateScopes + ValidateOnBuild

        Assert.NotNull(provider);
    }

    /// <summary>
    /// Composition is one decision, found where the consumer already looks. Every <c>Add*</c> over
    /// <see cref="IServiceCollection"/> lives in the libraries' root namespace (<see cref="Libraries.RootNamespace"/>),
    /// never in <c>Microsoft.Extensions.DependencyInjection</c> (that namespace is for Microsoft's packages)
    /// and never in a layer's sub-namespace, where a consumer would need a second <c>using</c> to find it.
    /// </summary>
    public static TheoryData<string> AddExtensionPlacements => AddExtensionRows();

    [Theory]
    [MemberData(nameof(AddExtensionPlacements))]
    public void Every_add_extension_lives_in_the_root_namespace(string verb)
    {
        MethodInfo method = Verbs().Single(candidate => Describe(candidate) == verb);

        Assert.Equal(Libraries.RootNamespace, method.DeclaringType!.Namespace);
    }

    /// <summary>
    /// The verbs on a builder an <c>Add*</c> returns live where the <c>Add*</c> does, in the root namespace: the builder
    /// arrives through the lambda, so a verb in a sub-namespace is invisible until the consumer guesses its <c>using</c>.
    /// A builder is a public interface named <c>IZabbix…Builder</c>.
    /// </summary>
    [Fact]
    public void Every_builder_verb_lives_in_the_root_namespace()
        => AssertNone(Libraries.PublicTypes
            .Where(type => type.IsAbstract && type.IsSealed)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.IsDefined(typeof(ExtensionAttribute), false)
                             && method.GetParameters() is [{ ParameterType: { IsInterface: true, Name: var receiver } }, ..]
                             && receiver.StartsWith("IZabbix", StringComparison.Ordinal) && receiver.EndsWith("Builder", StringComparison.Ordinal)
                             && method.DeclaringType!.Namespace != Libraries.RootNamespace)
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")
            .Distinct());

    /// <summary>
    /// Every public collection-like type implements the read-only interface (<see cref="IReadOnlyCollection{T}"/>),
    /// so a caller never needs a copy to count it. A type is collection-like when it implements <see cref="IEnumerable{T}"/>.
    /// </summary>
    [Fact]
    public void Public_enumerable_types_implement_read_only_collection()
    {
        List<string> offenders = [.. Libraries.PublicTypes
            .Where(type => !type.IsInterface)
            .Where(type => type.GetInterfaces().Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
            .Where(type => !type.GetInterfaces().Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>)))
            .Select(type => type.FullName!)];

        AssertNone(offenders);
    }

    /// <summary>
    /// No public signature exposes a mutable collection (<see cref="List{T}"/>, <see cref="Dictionary{TKey,TValue}"/>,
    /// <see cref="HashSet{T}"/>, a queue, a stack, an array) at any depth: CA1002/CA1819 see only the outer type, so a
    /// <c>Task&lt;List&lt;T&gt;&gt;</c> or a <c>Dictionary</c> passes them. Exempt, computed from the signature: a
    /// <c>params</c> array (the language's shape); a conversion operator and its CA2225 named alternate (the conversion
    /// from an array is the contract, and it copies); and the <see cref="JsonTypeInfo{T}"/> properties of a
    /// <see cref="JsonSerializerContext"/> (the System.Text.Json generator's contract, one per serialized type).
    /// </summary>
    [Fact]
    public void No_public_signature_exposes_a_mutable_collection()
        => AssertNone(Waivers.Outstanding(nameof(No_public_signature_exposes_a_mutable_collection), MutableCollectionExposures(Libraries.PublicTypes)));

    [Fact]
    public void The_mutable_collection_rule_sees_a_nested_list_and_a_dictionary()
        => Assert.Equal(["MutableCollectionProbe.Index: Dictionary`2", "MutableCollectionProbe.ItemsAsync(): List`1"], MutableCollectionExposures([typeof(MutableCollectionProbe)]).Order(StringComparer.Ordinal));

    /// <summary>
    /// Every options type is validated, by a library <see cref="IValidateOptions{TOptions}"/> the libraries use (register
    /// or construct: an IL mention).
    /// An options type is computed, not listed: every library <c>T</c> a library constructor takes through the options
    /// pattern (<see cref="IOptions{TOptions}"/>, <see cref="IOptionsMonitor{TOptions}"/>, <see cref="IOptionsSnapshot{TOptions}"/>),
    /// every library <c>T</c> an <c>Add*</c> container configures (<see cref="IConfigureOptions{TOptions}"/>), and every
    /// library class with a parameterless constructor that a public method configures through an <c>Action&lt;T&gt;</c>.
    /// </summary>
    public static TheoryData<string> OptionsTypes => [.. OptionsTypeSet().Select(type => type.FullName!).Order(StringComparer.Ordinal).Select(options => Waivers.Row(nameof(Every_options_type_has_a_validator_the_libraries_use), options))];

    [Theory]
    [MemberData(nameof(OptionsTypes))]
    public void Every_options_type_has_a_validator_the_libraries_use(string options)
    {
        Type type = OptionsTypeSet().Single(candidate => candidate.FullName == options);
        Type contract = typeof(IValidateOptions<>).MakeGenericType(type);
        List<Type> validators = [.. Libraries.Types.Where(candidate => !candidate.IsAbstract && contract.IsAssignableFrom(candidate))];

        Assert.Contains(validators, validator => CallGraph.Of.Methods.Any(method => CallGraph.Of.Instructions(method).Any(instruction => Mentions(instruction.Operand, validator))));
    }

    /// <summary>Whether an IL operand is <paramref name="type"/>, a member of it, or a generic instantiation over it (a registration).</summary>
    private static bool Mentions(object operand, Type type) => operand switch
    {
        Type mentioned => mentioned == type || (mentioned.IsGenericType && mentioned.GetGenericArguments().Contains(type)),
        MethodBase method => method.DeclaringType == type || (method.IsGenericMethod && method.GetGenericArguments().Contains(type)),
        _ => false,
    };

    public static TheoryData<string, string> WaivedViolations => Waivers.Rows();

    /// <summary>Each row is a waived violation (<see cref="Waivers"/>), skipped with its owner so the run lists it.</summary>
    [Theory]
    [MemberData(nameof(WaivedViolations))]
    public void A_waived_violation_is_still_owned(string rule, string offender)
        => Assert.Fail($"{rule}: {offender} is waived and must be skipped; a waiver that runs has lost its Skip");

    /// <summary>Fails with every offender, one per line (Assert.Empty truncates the list a fix needs).</summary>
    internal static void AssertNone(IEnumerable<string> offenders)
    {
        List<string> found = [.. offenders];
        if (found.Count > 0) Assert.Fail($"{found.Count} offender(s):\n{string.Join('\n', found)}");
    }

    // ---- the reflection beneath the rules ----

    internal static IEnumerable<string> UnsuffixedTaskMethods(IEnumerable<Type> types)
    {
        HashSet<MethodInfo> entryPoints = [.. Libraries.All.Select(library => library.EntryPoint).OfType<MethodInfo>()];
        return types
            .Where(type => !IsCompilerGenerated(type) && !typeof(Delegate).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(Declared))
            .Where(method => !method.IsSpecialName && !IsCompilerGenerated(method) && !method.Name.Contains('<', StringComparison.Ordinal))
            .Where(method => ReturnsTask(method.ReturnType))
            .Where(method => !method.Name.EndsWith("Async", StringComparison.Ordinal))
            // The asynchronous dispose pattern names its protected virtual core so (Learn, "Implement a DisposeAsync method").
            .Where(method => method.Name != "DisposeAsyncCore")
            .Where(method => !ImplementsForeignContract(method) && !entryPoints.Contains(method))
            .Select(Describe);
    }

    internal static IEnumerable<string> StaticMutableState(IEnumerable<Type> types)
    {
        List<Type> declared = [.. types.Where(type => !IsCompilerGenerated(type))];
        IEnumerable<string> fields = declared
            .SelectMany(type => type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(field => !field.IsLiteral && (!field.IsInitOnly || HoldsMutableValue(field)))
            .Select(field => $"{field.DeclaringType!.FullName}.{field.Name}");
        IEnumerable<string> properties = declared
            .SelectMany(type => type.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(property => property.SetMethod is not null)
            .Select(property => $"{property.DeclaringType!.FullName}.{property.Name} {{ set; }}");
        return fields.Concat(properties);
    }

    internal static IEnumerable<string> MutableCollectionExposures(IEnumerable<Type> types)
    {
        const BindingFlags surface = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (Type type in types.Where(type => !typeof(Delegate).IsAssignableFrom(type)))
        {
            bool context = typeof(JsonSerializerContext).IsAssignableFrom(type);
            foreach (PropertyInfo property in type.GetProperties(surface))
            {
                if (context && property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>)) continue;
                if (Mutable(property.PropertyType) is { } found) yield return $"{type.Name}.{property.Name}: {found.Name}";
            }
            foreach (FieldInfo field in type.GetFields(surface))
                if (Mutable(field.FieldType) is { } found) yield return $"{type.Name}.{field.Name}: {found.Name}";
            HashSet<(Type, Type)> conversions = [.. type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name is "op_Implicit" or "op_Explicit")
                .Select(method => (method.GetParameters()[0].ParameterType, method.ReturnType))];
            foreach (MethodBase method in type.GetMethods(surface).Where(method => !method.IsSpecialName).Cast<MethodBase>().Concat(type.GetConstructors()))
            {
                if (method is MethodInfo info && ImplementsForeignContract(info)) continue;
                // A conversion operator's named alternate (CA2225) has the operator's signature: the conversion is the contract.
                if (method is MethodInfo { IsStatic: true } alternate && alternate.GetParameters() is [{ ParameterType: var from }] && conversions.Contains((from, alternate.ReturnType))) continue;
                if (method is MethodInfo { ReturnType: var returns } && Mutable(returns) is { } returned)
                    yield return $"{type.Name}.{method.Name}(): {returned.Name}";
                foreach (ParameterInfo parameter in method.GetParameters().Where(parameter => !parameter.IsDefined(typeof(ParamArrayAttribute))))
                    if (Mutable(parameter.ParameterType) is { } found) yield return $"{type.Name}.{method.Name}({parameter.Name}): {found.Name}";
            }
        }
    }

    private static readonly Type[] MutableCollections = [typeof(List<>), typeof(Dictionary<,>), typeof(HashSet<>), typeof(SortedSet<>), typeof(SortedDictionary<,>), typeof(SortedList<,>), typeof(Queue<>), typeof(Stack<>), typeof(LinkedList<>)];

    /// <summary>The mutable collection inside <paramref name="type"/>, at any depth of its generic arguments, or null.</summary>
    private static Type? Mutable(Type type)
    {
        if (type.IsByRef || type.IsPointer) type = type.GetElementType()!;
        if (type.IsArray) return type;
        if (!type.IsGenericType) return null;
        if (MutableCollections.Contains(type.GetGenericTypeDefinition())) return type;
        return type.GetGenericArguments().Select(Mutable).FirstOrDefault(found => found is not null);
    }

    private static bool HoldsMutableValue(FieldInfo field)
    {
        if (field.DeclaringType!.ContainsGenericParameters) return field.FieldType.IsArray || Mutable(field.FieldType) is not null;
        object? value = field.GetValue(null);
        if (value is Array array) return array.Length > 0;
        if (value is null) return false;
        Type? collection = value.GetType().GetInterfaces().FirstOrDefault(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(ICollection<>));
        if (collection is not null) return !(bool)collection.GetProperty(nameof(ICollection<int>.IsReadOnly))!.GetValue(value)!;
        return value is IList { IsReadOnly: false } or IDictionary { IsReadOnly: false };
    }

    /// <summary>
    /// An implementation of an interface member, or an override of a virtual, whose declaration lives outside the libraries:
    /// its name and shape are that contract's (<c>IAsyncDisposable.DisposeAsync</c>, <c>BackgroundService.ExecuteAsync</c>,
    /// <c>IAsyncEnumerable&lt;T&gt;.GetAsyncEnumerator</c>, <c>HttpMessageHandler.SendAsync</c>).
    /// </summary>
    internal static bool ImplementsForeignContract(MethodInfo method)
    {
        if (method.GetBaseDefinition() is { } root && root != method && !Libraries.Contains(root.Module.Assembly)) return true;
        Type owner = method.DeclaringType!;
        if (owner.IsInterface) return false;
        foreach (Type contract in owner.GetInterfaces().Where(contract => !Libraries.Contains(contract.Assembly)))
        {
            InterfaceMapping map;
            try { map = owner.GetInterfaceMap(contract); }
            catch (ArgumentException) { continue; }
            if (map.TargetMethods.Contains(method)) return true;
        }
        return false;
    }

    internal static bool IsCompilerGenerated(MemberInfo member)
        => member.IsDefined(typeof(CompilerGeneratedAttribute), false) || (member is Type { DeclaringType: { } outer } && IsCompilerGenerated(outer));

    private static IEnumerable<MethodInfo> Verbs()
        => Libraries.PublicTypes
            .Where(type => type.IsAbstract && type.IsSealed)                              // static classes
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.IsDefined(typeof(ExtensionAttribute), false)
                             && method.Name.StartsWith("Add", StringComparison.Ordinal)
                             && method.GetParameters() is [{ ParameterType: var receiver }, ..] && receiver == typeof(IServiceCollection));

    private static TheoryData<string> AddExtensionRows()
    {
        var rows = new TheoryData<string>();
        foreach (MethodInfo verb in Verbs()) rows.Add(Describe(verb));
        return rows;
    }

    /// <summary>What each <c>Add*</c> registers, invoked once per verb with synthesised arguments.</summary>
    private static IEnumerable<List<ServiceDescriptor>> Containers()
    {
        foreach (MethodInfo verb in Verbs())
        {
            var services = new ServiceCollection();
            verb.Invoke(null, [services, .. verb.GetParameters().Skip(1).Select(Synthesize)]);
            yield return [.. services];
        }
    }

    private static readonly Type[] OptionsPattern = [typeof(IOptions<>), typeof(IOptionsMonitor<>), typeof(IOptionsSnapshot<>)];

    private static HashSet<Type> OptionsTypeSet()
    {
        IEnumerable<Type> taken = Libraries.Types
            .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Where(type => type.IsGenericType && OptionsPattern.Contains(type.GetGenericTypeDefinition()))
            .Select(type => type.GetGenericArguments()[0]);
        IEnumerable<Type> configured = Containers()
            .SelectMany(registered => registered)
            .Select(descriptor => descriptor.ServiceType)
            .Where(service => service.IsGenericType && service.GetGenericTypeDefinition() == typeof(IConfigureOptions<>))
            .Select(service => service.GetGenericArguments()[0]);
        IEnumerable<Type> configuredByCallback = Libraries.PublicTypes
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Action<>))
            .Select(type => type.GetGenericArguments()[0])
            .Where(type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false } && type.GetConstructor(Type.EmptyTypes) is not null)
            // a get's parameters, and a create's or an update's request, are one call's request, not options (ADR 0013, section 4)
            .Where(type => !type.Name.EndsWith("Parameters", StringComparison.Ordinal) && !type.Name.EndsWith("Request", StringComparison.Ordinal));
        return [.. taken.Concat(configured).Concat(configuredByCallback).Where(options => !options.IsGenericParameter && Libraries.Contains(options.Assembly))];
    }

    internal static bool ReturnsTask(Type type)
        => type == typeof(Task) || type == typeof(ValueTask)
           || (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)));

    internal static string Describe(MethodBase method)
        => $"{method.DeclaringType!.Name}.{method.Name}({string.Join(", ", method.GetParameters().Skip(method.IsDefined(typeof(ExtensionAttribute), false) ? 1 : 0).Select(parameter => parameter.ParameterType.ToString()))})";

    // An argument for a verb or a constructor, synthesised from its type alone: the container is validated, never used,
    // so the values only have to be well-formed, the same trick a DI container plays when it validates on build.
    internal static object? Synthesize(ParameterInfo parameter)
    {
        Type type = parameter.ParameterType;
        if (parameter.HasDefaultValue) return parameter.DefaultValue;
        if (type == typeof(string)) return "conformance";
        if (type == typeof(int)) return 10051;
        if (type == typeof(long)) return 1L;
        if (type == typeof(ulong)) return 1UL;
        if (type == typeof(ZabbixClock)) return new ZabbixClock(1, 0);
        if (type.IsEnum) return Enum.GetValues(type).GetValue(0);                     // any declared member is well-formed
        if (type == typeof(Uri)) return new Uri("http://conformance.invalid/api_jsonrpc.php");
        if (type == typeof(TimeProvider)) return new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        if (type.IsAssignableFrom(typeof(IConfigurationRoot))) return new ConfigurationBuilder().Build();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ChannelWriter<>))
        {
            object channel = typeof(Channel).GetMethod(nameof(Channel.CreateUnbounded), 1, Type.EmptyTypes)!.MakeGenericMethod(type.GetGenericArguments()).Invoke(null, null)!;
            return channel.GetType().GetProperty(nameof(Channel<int>.Writer))!.GetValue(channel);
        }
        if (typeof(Delegate).IsAssignableFrom(type))
        {
            MethodInfo invoke = type.GetMethod("Invoke")!;
            ParameterExpression[] parameters = [.. invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name))];
            Expression body = invoke.ReturnType == typeof(void) ? Expression.Empty() : Expression.Default(invoke.ReturnType);
            return Expression.Lambda(type, body, parameters).Compile();
        }
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.UnixEpoch;
        if (!type.IsValueType && new NullabilityInfoContext().Create(parameter).WriteState == NullabilityState.Nullable) return null;
        throw new NotSupportedException($"no synthesis for a {type} argument — extend Synthesize when a verb or constructor grows one");
    }

    internal static IEnumerable<(MethodInfo Method, LoggerMessageAttribute Attribute)> LoggerMessages()
        => Libraries.Types
            .SelectMany(type => type.GetMethods(Declared))
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(entry => entry.Attribute is not null)
            .Select(entry => (entry.Method, entry.Attribute!));
}

/// <summary>A violation the async-suffix rule must find: a task-returning method without the suffix (and without <c>async</c>, so IDE1006 cannot).</summary>
internal sealed class AsyncSuffixProbe
{
    public static Task Poll() => Task.CompletedTask;

    public static Task PollAsync() => Task.CompletedTask;
}

/// <summary>Violations the mutable-collection rule must find, at depth and beside the direct forms CA1002 sees.</summary>
public sealed class MutableCollectionProbe
{
#pragma warning disable CA2227, CA1002 // the probe IS the violation
    public Dictionary<string, int> Index { get; } = [];
#pragma warning restore CA2227, CA1002

    public IReadOnlyList<int> Fine { get; } = [];

    public static Task<List<int>> ItemsAsync() => Task.FromResult<List<int>>([]);

    public static int Sum(params int[] values) => values.Sum();
}

/// <summary>Violations the static-state rule must find.</summary>
internal static class StaticStateProbe
{
#pragma warning disable CA2211 // the probe IS the violation
    public static readonly int[] Codes = [1, 2];
#pragma warning restore CA2211

    public static int Current { get; set; }
}

/// <summary>The repository root (the folder holding <c>Zabbix.slnx</c>), found from the test assembly's location.</summary>
internal static class Repository
{
    public static string Root { get; } = Find();

    private static string Find()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Zabbix.slnx")))
            directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("Zabbix.slnx not found above the test assembly");
    }
}
