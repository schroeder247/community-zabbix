// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The design-conformance rules, read over the module.
/// <c>Community.Zabbix.Tests.DesignConformanceTests</c> computes its assemblies from its own project references, which do not include
/// <c>Zabbix.PowerShell</c> (the module needs the PowerShell SDK), so none of its rules reads the module. These are
/// the same rules, over this assembly, with the module's own idiom carved out by computation, never by name: a
/// cmdlet's <c>[Parameter]</c> arrays are PowerShell's collection currency, a cmdlet overrides the engine's synchronous
/// contract. Plus the module's own secret rule, with the spelling Zabbix itself uses (<c>passwd</c>).
/// </summary>
public sealed partial class AdversarialPowerShellConformanceTests
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    // The three modules (ADR 0003): every rule here holds for each of them.
    private static readonly Assembly[] Modules = [.. ModuleUnderTest.All.Select(module => module.Assembly)];

    private static readonly string[] ForbiddenSuffixes = ["Manager", "Helper", "Coordinator", "Orchestrator", "Engine", "Util", "Utils", "Impl"];

    private static IEnumerable<Type> Types => Modules.SelectMany(module => module.GetTypes()).Where(type => !IsCompilerGenerated(type));

    private static IEnumerable<Type> Cmdlets => ExportedTypes.Where(type => !type.IsAbstract && type.GetCustomAttribute<CmdletAttribute>() is not null);

    [Fact]
    public void No_module_type_name_has_a_forbidden_suffix()
        => AssertNone(Types.Where(type => ForbiddenSuffixes.Any(suffix => type.Name.EndsWith(suffix, StringComparison.Ordinal))).Select(type => type.FullName!));

    [Fact]
    public void Every_task_returning_module_method_name_ends_in_async()
        => AssertNone(Types
            .Where(type => !typeof(Delegate).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(Declared))
            .Where(method => !method.IsSpecialName && !IsCompilerGenerated(method) && !method.Name.Contains('<', StringComparison.Ordinal))
            .Where(method => ReturnsTask(method.ReturnType) && !method.Name.EndsWith("Async", StringComparison.Ordinal))
            .Where(method => !ImplementsForeignContract(method))
            .Select(Describe));

    [Fact]
    public void Every_task_returning_public_module_method_takes_a_cancellation_token_last()
        => AssertNone(ExportedTypes
            .Where(type => !typeof(Delegate).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => !method.IsSpecialName && ReturnsTask(method.ReturnType) && !ImplementsForeignContract(method))
            .Where(method => method.GetParameters() is not [.., { ParameterType: var last }] || last != typeof(CancellationToken))
            .Select(Describe));

    /// <summary>
    /// Event ids are unique across EVERY assembly that logs into one host — the module's and the SDK's together (a
    /// PowerShell session logs both through the same provider); the SDK-side rule never saw the module's ids.
    /// </summary>
    [Fact]
    public void Logger_message_event_ids_are_unique_across_the_module_and_the_sdk()
    {
        List<(Assembly Assembly, MethodInfo Method, LoggerMessageAttribute Attribute)> messages = [.. SdkAndModule()
            .SelectMany(assembly => assembly.GetTypes().SelectMany(type => type.GetMethods(Declared))
                .Select(method => (Assembly: assembly, Method: method, Attribute: method.GetCustomAttribute<LoggerMessageAttribute>()!))
                .Where(entry => entry.Attribute is not null))];
        Assert.Contains(messages, entry => entry.Assembly == ModuleUnderTest.Zabbix.Assembly);

        AssertNone(messages
            .GroupBy(entry => entry.Attribute.EventId)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(entry => $"{entry.Assembly.GetName().Name}:{entry.Method.DeclaringType!.Name}.{entry.Method.Name}"))}"));
    }

    /// <summary>
    /// The module's half of <c>Every_warning_or_error_event_is_asserted_by_a_test</c>, which does not read the module:
    /// every <c>[LoggerMessage]</c> of the module at Warning or above is asserted by a test of this project (its id appears
    /// as an integer literal in a test source), so an operator-facing warning cannot silently change or disappear.
    /// </summary>
    [Fact]
    public void Every_warning_or_error_event_of_the_module_is_asserted_by_a_test()
    {
        string tests = string.Join('\n', Directory.EnumerateFiles(TestSources(), "*.cs").Where(file => !file.EndsWith(nameof(AdversarialPowerShellConformanceTests) + ".cs", StringComparison.Ordinal)).Select(File.ReadAllText));
        List<LoggerMessageAttribute> warnings = [.. Types.SelectMany(type => type.GetMethods(Declared))
            .Select(method => method.GetCustomAttribute<LoggerMessageAttribute>())
            .OfType<LoggerMessageAttribute>()
            .Where(attribute => attribute.Level >= LogLevel.Warning)];
        Assert.NotEmpty(warnings);

        AssertNone(warnings
            .Where(attribute => !Regex.IsMatch(tests, $@"\b{attribute.EventId}\b", RegexOptions.CultureInvariant))
            .Select(attribute => $"{attribute.EventId} {attribute.Message}"));
    }

    private static string TestSources()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Zabbix.PowerShell.Tests.csproj"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("the test project's sources are not above the test assembly");
    }

    /// <summary>
    /// No static mutable state in the module (one process hosts many runspaces). Exempt, computed: a
    /// <c>static readonly</c> concurrent memo keyed by <see cref="Type"/> (type metadata, a pure function of its key).
    /// A writable static array or a process-wide counter would be shared by every runspace.
    /// </summary>
    [Fact]
    public void No_static_mutable_state_in_the_module()
        => AssertNone(StaticMutableState(Types));

    /// <summary>
    /// No public signature exposes a mutable collection. Carved out by computation: a cmdlet's properties (its
    /// parameters and the abstract slots its bases declare for them: arrays and hashtables are how PowerShell binds many
    /// values; <c>GlobalSuppressions.cs</c> records CA1819 for them) and an override of an engine member.
    /// </summary>
    [Fact]
    public void No_public_module_signature_exposes_a_mutable_collection()
        => AssertNone(MutableCollectionExposures(ExportedTypes));

    [Fact]
    public void Public_module_classes_are_sealed_or_abstract()
        => AssertNone(ExportedTypes
            .Where(type => type is { IsClass: true, IsSealed: false, IsAbstract: false } && !typeof(Delegate).IsAssignableFrom(type))
            .Select(type => type.FullName!));

    /// <summary>
    /// PSScriptAnalyzer <c>PSAvoidUsingPlainTextForPassword</c>: every parameter that carries a secret
    /// is a <see cref="SecureString"/> or a <see cref="PSCredential"/> — with the spelling Zabbix itself uses. The existing
    /// <c>ModuleShapeTests.A_secret_is_never_a_positional_string</c> and <c>ZabbixCmdletEmitter.SecretName</c> share one
    /// pattern (<c>password|passphrase|token|secret|psk|apikey</c>) that does not match <c>passwd</c>, the field name of
    /// <c>user.update</c> and <c>mediatype</c>: <c>Set-ZbxUser -Passwd/-CurrentPasswd</c> and <c>Set-ZbxMediaType -Passwd</c>
    /// take the password as a plain string (history, transcripts, <c>$PSBoundParameters</c> logging).
    /// </summary>
    [Fact]
    public void Every_secret_parameter_is_a_secure_string_or_a_credential()
        => AssertNone(CmdletParameters()
            .Where(parameter => SecretParameter().IsMatch(parameter.Property.Name) && !IsSecretType(parameter.Property.PropertyType))
            .Select(parameter => $"{Noun(parameter.Cmdlet)} -{parameter.Property.Name}: {parameter.Property.PropertyType.Name}"));

    /// <summary>
    /// The reverse: a URL is not a <see cref="SecureString"/>. A pattern that matches <c>token</c> anywhere would make
    /// <c>New-/Set-ZbxMediaType -TokenUrl</c> (an OAuth endpoint URL, <c>token_url</c>) ask for a
    /// <see cref="SecureString"/>, and the user would have to <c>ConvertTo-SecureString -AsPlainText</c> a URL. (<c>-TlsPskIdentity</c> is
    /// a <see cref="SecureString"/> on the generated host/proxy cmdlets but a string on <c>Set-ZbxAutoregistration</c>; upstream
    /// makes <c>tls_psk_identity</c> write-only, so it is not held to this rule.)
    /// </summary>
    [Fact]
    public void No_url_parameter_is_a_secure_string()
        => AssertNone(CmdletParameters()
            .Where(parameter => IsSecretType(parameter.Property.PropertyType) && NotASecret().IsMatch(parameter.Property.Name))
            .Select(parameter => $"{Noun(parameter.Cmdlet)} -{parameter.Property.Name}"));

    [Fact]
    public void The_secret_parameter_rules_see_the_passwd_spelling_and_a_url()
    {
        Assert.Matches(SecretParameter(), "CurrentPasswd");
        Assert.DoesNotMatch(SecretParameter(), "TokenUrl");
        Assert.DoesNotMatch(SecretParameter(), "TlsPskIdentity");
        Assert.Matches(NotASecret(), "TokenUrl");
    }

    // ================================================================================================================

    [GeneratedRegex(@"(password|passwd|passphrase|secret|apikey|psk|token)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretParameter();

    [GeneratedRegex(@"(url|uri|endpoint)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotASecret();

    private static bool IsSecretType(Type type) => type == typeof(SecureString) || type == typeof(PSCredential);

    private static IEnumerable<(Type Cmdlet, PropertyInfo Property)> CmdletParameters()
        => Cmdlets.SelectMany(type => type.GetProperties().Where(property => property.GetCustomAttributes<ParameterAttribute>().Any()).Select(property => (type, property)));

    private static string Noun(Type cmdlet) => cmdlet.GetCustomAttribute<CmdletAttribute>() is { } attribute ? $"{attribute.VerbName}-{attribute.NounName}" : cmdlet.Name;

    private static IEnumerable<Assembly> SdkAndModule()
        => Modules.Concat(Modules.SelectMany(module => module.GetReferencedAssemblies())
            .Where(reference => reference.Name!.StartsWith("Community.Zabbix.", StringComparison.Ordinal))
            .Select(Assembly.Load))
            .Distinct();

    private static IEnumerable<Type> ExportedTypes => Modules.SelectMany(module => module.GetExportedTypes());

    private static void AssertNone(IEnumerable<string> offenders)
    {
        List<string> found = [.. offenders];
        if (found.Count > 0) Assert.Fail($"{found.Count} offender(s):\n{string.Join('\n', found)}");
    }

    private static bool IsCompilerGenerated(MemberInfo member)
        => member.IsDefined(typeof(CompilerGeneratedAttribute), false) || (member.DeclaringType is { } owner && IsCompilerGenerated(owner));

    private static bool ReturnsTask(Type type)
        => type == typeof(Task) || type == typeof(ValueTask)
           || (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)));

    private static string Describe(MethodBase method)
        => $"{method.DeclaringType!.Name}.{method.Name}({string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name))})";

    /// <summary>An override or implementation of a member declared outside the module: its shape is that contract's.</summary>
    private static bool ImplementsForeignContract(MethodInfo method)
    {
        if (method.GetBaseDefinition() is { } root && root != method && !Modules.Contains(root.Module.Assembly)) return true;
        Type owner = method.DeclaringType!;
        if (owner.IsInterface) return false;
        return owner.GetInterfaces().Where(contract => !Modules.Contains(contract.Assembly)).Any(contract =>
        {
            InterfaceMapping map;
            try { map = owner.GetInterfaceMap(contract); }
            catch (ArgumentException) { return false; }
            return map.TargetMethods.Contains(method);
        });
    }

    private static IEnumerable<string> StaticMutableState(IEnumerable<Type> types)
    {
        List<Type> declared = [.. types];
        IEnumerable<string> fields = declared
            .SelectMany(type => type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(field => !field.IsLiteral && (!field.IsInitOnly || HoldsMutableValue(field)) && !IsTypeMemo(field))
            .Select(field => $"{field.DeclaringType!.FullName}.{field.Name}");
        IEnumerable<string> properties = declared
            .SelectMany(type => type.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(property => property.SetMethod is not null)
            .Select(property => $"{property.DeclaringType!.FullName}.{property.Name} {{ set; }}");
        return fields.Concat(properties);
    }

    private static bool IsTypeMemo(FieldInfo field)
        => field.IsInitOnly && field.FieldType.IsGenericType
           && field.FieldType.GetGenericTypeDefinition() == typeof(System.Collections.Concurrent.ConcurrentDictionary<,>)
           && field.FieldType.GetGenericArguments()[0] == typeof(Type);

    private static bool HoldsMutableValue(FieldInfo field)
    {
        if (field.DeclaringType!.ContainsGenericParameters) return field.FieldType.IsArray || Mutable(field.FieldType) is not null;
        object? value;
        try { value = field.GetValue(null); }
        catch (TargetInvocationException) { return false; }
        if (value is Array array) return array.Length > 0;
        if (value is null) return false;
        Type? collection = value.GetType().GetInterfaces().FirstOrDefault(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(ICollection<>));
        if (collection is not null) return !(bool)collection.GetProperty(nameof(ICollection<int>.IsReadOnly))!.GetValue(value)!;
        return value is IList { IsReadOnly: false } or IDictionary { IsReadOnly: false };
    }

    private static readonly Type[] MutableCollections = [typeof(List<>), typeof(Dictionary<,>), typeof(HashSet<>), typeof(SortedSet<>), typeof(SortedDictionary<,>), typeof(SortedList<,>), typeof(Queue<>), typeof(Stack<>), typeof(LinkedList<>)];

    private static Type? Mutable(Type type)
    {
        if (type.IsByRef || type.IsPointer) type = type.GetElementType()!;
        if (type.IsArray) return type;
        if (!type.IsGenericType) return null;
        if (MutableCollections.Contains(type.GetGenericTypeDefinition())) return type;
        return type.GetGenericArguments().Select(Mutable).FirstOrDefault(found => found is not null);
    }

    private static IEnumerable<string> MutableCollectionExposures(IEnumerable<Type> types)
    {
        const BindingFlags surface = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (Type type in types.Where(type => !typeof(Delegate).IsAssignableFrom(type)))
        {
            foreach (PropertyInfo property in type.GetProperties(surface))
            {
                if (typeof(Cmdlet).IsAssignableFrom(type)) break;                                            // a cmdlet's properties are its parameters: PowerShell's binding currency
                if (property.GetMethod is { } getter && ImplementsForeignContract(getter)) continue;
                if (Mutable(property.PropertyType) is { } found) yield return $"{type.Name}.{property.Name}: {found.Name}";
            }
            foreach (FieldInfo field in type.GetFields(surface))
                if (Mutable(field.FieldType) is { } found) yield return $"{type.Name}.{field.Name}: {found.Name}";
            foreach (MethodBase method in type.GetMethods(surface).Where(method => !method.IsSpecialName).Cast<MethodBase>().Concat(type.GetConstructors()))
            {
                if (method is MethodInfo info && ImplementsForeignContract(info)) continue;
                if (method is MethodInfo { ReturnType: var returns } && Mutable(returns) is { } returned) yield return $"{type.Name}.{method.Name}(): {returned.Name}";
                foreach (ParameterInfo parameter in method.GetParameters().Where(parameter => !parameter.IsDefined(typeof(ParamArrayAttribute))))
                    if (Mutable(parameter.ParameterType) is { } found) yield return $"{type.Name}.{method.Name}({parameter.Name}): {found.Name}";
            }
        }
    }
}
