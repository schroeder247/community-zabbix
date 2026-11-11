// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using Community.Zabbix.Tests.Conformance;

namespace Community.Zabbix.Tests;

/// <summary>
/// A public constructor of the client libraries takes no default arguments: the shorter forms are overloads that call the
/// longest one, as the Framework Design Guidelines' member overloading asks ("DO make only the longest overload virtual",
/// "DO allow null to be passed for optional arguments"). A default value is compiled into the caller, so changing it, or
/// adding a parameter after it, breaks a compiled caller; an overload does neither.
/// </summary>
public sealed partial class DesignConformanceTests
{
    private static readonly HashSet<string> OverloadedLibraries = new(StringComparer.Ordinal) { "Community.Zabbix.Abstractions", "Community.Zabbix.Api", "Community.Zabbix.Sdk", "Community.Zabbix.Wire" };

    [Fact]
    public void No_public_constructor_of_a_client_library_takes_a_default_argument()
        => AssertNone(DefaultedConstructors(Libraries.PublicTypes.Where(type => OverloadedLibraries.Contains(type.Assembly.GetName().Name!))));

    [Fact]
    public void The_overload_rule_finds_a_defaulted_constructor()
        => Assert.Equal(["DefaultedConstructorProbe(System.String, System.Int32)"], DefaultedConstructors([typeof(DefaultedConstructorProbe)]));

    internal static IEnumerable<string> DefaultedConstructors(IEnumerable<Type> types)
        => types
            .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            .Where(constructor => !constructor.DeclaringType!.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)))
            .Where(constructor => !IsRecordPrimaryConstructor(constructor))
            .Where(constructor => constructor.GetParameters().Any(parameter => parameter.HasDefaultValue))
            .Select(constructor => $"{constructor.DeclaringType!.Name}({string.Join(", ", constructor.GetParameters().Select(parameter => parameter.ParameterType))})")
            .Order(StringComparer.Ordinal);

    // A positional record's primary constructor is its deconstruction's mirror; its defaults are the record's shape.
    private static bool IsRecordPrimaryConstructor(ConstructorInfo constructor)
        => constructor.DeclaringType!.GetMethod("<Clone>$") is not null
            && constructor.DeclaringType.GetMethod("Deconstruct") is { } deconstruct
            && deconstruct.GetParameters().Length == constructor.GetParameters().Length;
}

/// <summary>A constructor the overload rule must find: a defaulted parameter.</summary>
public sealed class DefaultedConstructorProbe
{
    /// <summary>Initializes the probe.</summary>
    /// <param name="name">A name.</param>
    /// <param name="port">A port.</param>
    public DefaultedConstructorProbe(string name, int port = 0) => (Name, Port) = (name, port);

    /// <summary>Gets the name.</summary>
    public string Name { get; }

    /// <summary>Gets the port.</summary>
    public int Port { get; }
}
