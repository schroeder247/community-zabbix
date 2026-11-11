// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The modules program against the SDK's public API, as an application does: no library grants them its internals,
/// and from <c>Community.Zabbix.Abstractions</c>, which does, they take only the telemetry and error-type plumbing of
/// <c>Zabbix.Diagnostics</c>, since they are a layer of the same telemetry. Read from each module's compiled metadata, so
/// generated code is held to it too.
/// </summary>
public sealed class ModulePublicApiTests
{
    private const string Contracts = "Community.Zabbix.Abstractions";
    private const string Plumbing = "Community.Zabbix.Diagnostics";

    private const BindingFlags Every = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [Theory]
    [InlineData("Community.Zabbix.Api")]
    [InlineData("Community.Zabbix.Sdk")]
    [InlineData("Community.Zabbix.Wire")]
    [InlineData("Community.Zabbix.Agent")]
    [InlineData("Community.Zabbix.Proxy")]
    [InlineData("Community.Zabbix.Agent.Plugins")]
    public void A_library_grants_the_modules_none_of_its_internals(string library)
    {
        Assembly assembly = Assembly.Load(library);

        Assert.DoesNotContain(assembly.GetCustomAttributes<InternalsVisibleToAttribute>(),
            grant => grant.AssemblyName.StartsWith("Community.Zabbix.PowerShell", StringComparison.Ordinal) && !grant.AssemblyName.EndsWith(".Tests", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Community.Zabbix.PowerShell")]
    [InlineData("Community.Zabbix.PowerShell.Agent")]
    [InlineData("Community.Zabbix.PowerShell.Proxy")]
    public void A_module_takes_only_the_diagnostics_plumbing_from_the_contracts_internals(string module)
    {
        (List<string> plumbing, List<string> offenders) = InternalUses(module);

        Assert.Empty(offenders);
        if (module == "Community.Zabbix.PowerShell") Assert.Contains("Community.Zabbix.Diagnostics.ZabbixLayerTelemetry", plumbing);   // the scan sees an internal use
    }

    // Every type and member of Community.Zabbix.Abstractions the module references that a caller outside the family could not:
    // a type that is not visible, or a member whose every overload is neither public nor protected.
    private static (List<string> Plumbing, List<string> Offenders) InternalUses(string module)
    {
        Assembly contracts = Assembly.Load(Contracts);
        using FileStream file = File.OpenRead(Assembly.Load(module).Location);
        using var image = new PEReader(file);
        MetadataReader metadata = image.GetMetadataReader();
        var plumbing = new List<string>();
        var offenders = new List<string>();

        void Note(Type type, string what) => (type.Namespace == Plumbing ? plumbing : offenders).Add(what);

        foreach (TypeReferenceHandle handle in metadata.TypeReferences)
        {
            if (Resolve(metadata, handle, contracts) is { IsVisible: false } type)
                Note(type, type.FullName!);
        }
        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            MemberReference member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference || Resolve(metadata, (TypeReferenceHandle)member.Parent, contracts) is not { } type) continue;
            string name = metadata.GetString(member.Name);
            MemberInfo[] candidates = type.GetMember(name, Every);
            if (candidates.Length > 0 && !candidates.Any(IsReachable))
                Note(type, type.FullName + "." + name);
        }
        return (plumbing, offenders);
    }

    // The type a reference names, when it is one of Zabbix.Abstractions'.
    private static Type? Resolve(MetadataReader metadata, TypeReferenceHandle handle, Assembly contracts)
    {
        TypeReference reference = metadata.GetTypeReference(handle);
        string name = metadata.GetString(reference.Name);
        switch (reference.ResolutionScope.Kind)
        {
            case HandleKind.TypeReference:
                return Resolve(metadata, (TypeReferenceHandle)reference.ResolutionScope, contracts)?.GetNestedType(name, Every);
            case HandleKind.AssemblyReference:
                AssemblyReference scope = metadata.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope);
                if (metadata.GetString(scope.Name) != Contracts) return null;
                string space = metadata.GetString(reference.Namespace);
                return contracts.GetType(space.Length == 0 ? name : space + "." + name, throwOnError: false);
            default:
                return null;
        }
    }

    private static bool IsReachable(MemberInfo member) => member switch
    {
        MethodBase method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly,
        FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
        PropertyInfo property => property.GetAccessors(nonPublic: true).Any(IsReachable),
        EventInfo @event => @event.AddMethod is { } add && IsReachable(add),
        Type nested => nested.IsVisible,
        _ => true,
    };
}
