// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Community.Zabbix.Tests;

/// <summary>
/// The protocol split's dependency policy (docs/adr/0006-protocol-split.md), read from each library's restore graph
/// (<c>obj/project.assets.json</c>, the closure NuGet resolved for it, transitive packages included): <c>Zabbix.Sdk</c>,
/// the JSON-RPC API, takes Microsoft.Extensions packages and the HTTP resilience pipeline and nothing of the wire, the
/// hosts, the listeners or a database; <c>Zabbix.Wire</c>, the framed TCP wire, takes the contracts, BouncyCastle, the
/// telemetry abstractions (the logging generator that honours a data classification), and the container and the options'
/// configuration binding its client's builder composes (docs/adr/0013-builders-verbs-and-typed-values.md), and nothing of
/// the API. The hosting
/// packages' compiled references are read too: the proxy's ASP.NET Core assemblies and <c>Zabbix.Api</c> types, and the
/// agent's and its EF Core store's frameworks.
/// </summary>
public sealed partial class DesignConformanceTests
{
    /// <summary>
    /// What the API package must never pull in, as package-id prefixes. <c>Microsoft.Extensions.Hosting</c> is matched
    /// exactly: <c>Microsoft.Extensions.Hosting.Abstractions</c> arrives through dotnet/extensions' own telemetry packages
    /// (<c>AmbientMetadata.Application</c>, <c>DependencyInjection.AutoActivation</c>) and is an abstraction; the generic
    /// host itself is a hosting package's choice.
    /// </summary>
    private static readonly string[] ApiForbiddenPrefixes =
    [
        "BouncyCastle", "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Microsoft.Extensions.ServiceDiscovery",
        "Microsoft.Extensions.Diagnostics.HealthChecks", "Npgsql", "SQLitePCLRaw", "YamlDotNet", "OpenTelemetry",
        "Community.Zabbix.Wire", "Community.Zabbix.Agent", "Community.Zabbix.Proxy",
    ];

    [Fact]
    public void The_api_package_takes_no_tcp_tls_hosting_listener_or_database_dependency()
    {
        IReadOnlyList<string> closure = Closure("Zabbix.Sdk");

        Assert.Contains("Microsoft.Extensions.Http.Resilience", closure);
        Assert.Equal([], [.. closure.Where(name => ApiForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))]);
        Assert.DoesNotContain("Microsoft.Extensions.Hosting", closure);
        Assert.Equal(["Microsoft.NETCore.App"], FrameworkReferences("Zabbix.Sdk"));
    }

    [Fact]
    public void The_wire_package_takes_the_contracts_and_bouncycastle_and_nothing_of_the_api()
    {
        IReadOnlyList<string> closure = Closure("Zabbix.Wire");

        Assert.Contains("BouncyCastle.Cryptography", closure);
        Assert.Contains("Community.Zabbix.Abstractions", closure);
        Assert.Contains("Microsoft.Extensions.Telemetry.Abstractions", closure);   // the extended generator: classified log parameters
        Assert.Contains("Microsoft.Extensions.DependencyInjection", closure);      // the builder's own container, built as a host's is
        Assert.DoesNotContain("Microsoft.Extensions.Telemetry", closure);          // the implementation is the application's
        Assert.Equal([], [.. closure.Where(name => name.StartsWith("Community.Zabbix.", StringComparison.Ordinal) && name != "Community.Zabbix.Abstractions")]);
        Assert.Equal([], [.. closure.Where(name => name.StartsWith("Microsoft.Extensions.Http", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal) || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) || name.StartsWith("Polly", StringComparison.Ordinal))]);
        Assert.Equal(["Microsoft.NETCore.App"], FrameworkReferences("Zabbix.Wire"));
    }

    /// <summary>
    /// The hosting packages send over the wire and never call the JSON-RPC API: neither takes <c>Zabbix.Sdk</c> or the HTTP
    /// pipeline, so an agent or a proxy does not carry the generated API clients and the HTTP resilience stack. The agent
    /// takes no API vocabulary either; the proxy takes <c>Zabbix.Api</c> for the generated words its listeners and connector
    /// speak (pinned by type below).
    /// </summary>
    [Theory]
    [InlineData("Zabbix.Agent")]
    [InlineData("Zabbix.Proxy")]
    [InlineData("Zabbix.Agent.Storage.EntityFrameworkCore")]
    public void A_hosting_package_takes_the_wire_and_nothing_of_the_api_client(string library)
    {
        IReadOnlyList<string> closure = Closure(library);

        Assert.Contains("Community.Zabbix.Wire", closure);
        Assert.DoesNotContain("Community.Zabbix.Sdk", closure);
        Assert.Equal([], [.. closure.Where(name => name.StartsWith("Microsoft.Extensions.Http", StringComparison.Ordinal))]);
        if (library != "Zabbix.Proxy") Assert.DoesNotContain("Community.Zabbix.Api", closure);
    }

    /// <summary>
    /// The proxy is the one library on the ASP.NET Core shared framework, for the connection layer its listeners accept on
    /// and the routing its connector endpoint maps onto the caller's application. Its compiled references name exactly the
    /// ASP.NET Core assemblies that serves: a new one (MVC, a web host, authentication) is a decision, not a drift. From
    /// <c>Zabbix.Api</c> it takes generated words, never a copy of one
    /// (docs/adr/0013-builders-verbs-and-typed-values.md, section 7): a listener's <c>ZabbixTlsAcceptModes</c>; the
    /// connector's <c>ZabbixTag</c> with its <c>ZabbixTagOrigin</c>, <c>ZabbixSeverity</c> and <c>ZabbixItemValueType</c>;
    /// the proxy configuration's <c>ZabbixHostStatus</c>, <c>ZabbixItemStatus</c> and <c>ZabbixItemType</c>; and the
    /// <c>ZabbixProxyOperatingMode</c> a configured proxy runs in.
    /// </summary>
    private static readonly string[] ProxyAspNetCoreAssemblies =
    [
        "Microsoft.AspNetCore.Connections.Abstractions", "Microsoft.AspNetCore.Http.Abstractions", "Microsoft.AspNetCore.Http.Extensions",
        "Microsoft.AspNetCore.Http.Features", "Microsoft.AspNetCore.Routing", "Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets",
    ];

    [Fact]
    public void The_proxy_takes_the_aspnetcore_connection_and_routing_layer_and_the_api_words_it_names_only()
    {
        (List<string> assemblies, List<string> apiTypes) = References("Zabbix.Proxy");

        Assert.Equal(["Microsoft.AspNetCore.App", "Microsoft.NETCore.App"], FrameworkReferences("Zabbix.Proxy"));
        Assert.Equal(ProxyAspNetCoreAssemblies.AsEnumerable(), assemblies.Where(name => name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)));
        Assert.Equal(
        [
            "Community.Zabbix.ZabbixHostStatus", "Community.Zabbix.ZabbixItemStatus", "Community.Zabbix.ZabbixItemType",
            "Community.Zabbix.ZabbixItemValueType", "Community.Zabbix.ZabbixProxyOperatingMode", "Community.Zabbix.ZabbixSeverity",
            "Community.Zabbix.ZabbixTag", "Community.Zabbix.ZabbixTagOrigin", "Community.Zabbix.ZabbixTlsAcceptModes",
        ], apiTypes);
    }

    /// <summary>
    /// The plugin loader takes the item contract and nothing else (docs/adr/0008-item-providers-and-plugins.md): no wire,
    /// no hosting, no API, so loading plugins adds no implementation package to an agent. A plugin brings its own.
    /// </summary>
    [Fact]
    public void The_plugin_loader_takes_the_contracts_and_nothing_else()
    {
        IReadOnlyList<string> closure = Closure("Zabbix.Agent.Plugins");
        (List<string> assemblies, List<string> apiTypes) = References("Zabbix.Agent.Plugins");

        Assert.Equal(["Community.Zabbix.Abstractions"], [.. closure.Where(name => name.StartsWith("Community.Zabbix.", StringComparison.Ordinal))]);
        Assert.Equal([], [.. closure.Where(name => !name.StartsWith("Community.Zabbix.", StringComparison.Ordinal) && !name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))]);
        Assert.DoesNotContain("Microsoft.Extensions.Hosting", closure);                     // the generic host; its abstractions are the contract's
        Assert.Equal([], [.. closure.Where(name => name.StartsWith("Microsoft.Extensions.Http", StringComparison.Ordinal))]);
        Assert.Equal(["Microsoft.NETCore.App"], FrameworkReferences("Zabbix.Agent.Plugins"));
        Assert.Equal([], [.. assemblies.Where(name => name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal))]);
        Assert.Equal([], apiTypes);
    }

    /// <summary>The agent and its EF Core store run on the base framework alone: no ASP.NET Core, no desktop framework.</summary>
    [Theory]
    [InlineData("Zabbix.Agent")]
    [InlineData("Zabbix.Agent.Storage.EntityFrameworkCore")]
    public void The_agent_and_its_ef_store_take_the_base_framework_only(string library)
    {
        (List<string> assemblies, List<string> apiTypes) = References(library);

        Assert.Equal(["Microsoft.NETCore.App"], FrameworkReferences(library));
        Assert.Equal([], [.. assemblies.Where(name => name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal))]);
        Assert.Equal([], apiTypes);
    }

    [Fact]
    public void The_contracts_take_abstractions_only()
    {
        IReadOnlyList<string> closure = Closure("Zabbix.Abstractions");

        Assert.All(closure, name => Assert.True(
            name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) && (name.EndsWith(".Abstractions", StringComparison.Ordinal)
                || name is "Microsoft.Extensions.Options" or "Microsoft.Extensions.Primitives" or "Microsoft.Extensions.ObjectPool"),
            $"Zabbix.Abstractions takes {name}"));
    }

    /// <summary>
    /// The package and project ids in a library's resolved closure, analyzers and build tasks left out. A family project
    /// appears under its package id (<c>Community.Zabbix.Wire</c>), not its folder or assembly name.
    /// </summary>
    private static List<string> Closure(string library)
    {
        using JsonDocument assets = Assets(library);
        return [.. assets.RootElement.GetProperty("targets").GetProperty("net10.0").EnumerateObject()
            .Select(entry => entry.Name.Split('/')[0])
            .Where(name => !name.StartsWith("Microsoft.CodeAnalysis.", StringComparison.Ordinal) && name is not "Microsoft.NET.ILLink.Tasks" and not "Microsoft.Sbom.Targets")
            .Order(StringComparer.Ordinal)];
    }

    private static List<string> FrameworkReferences(string library)
    {
        using JsonDocument assets = Assets(library);
        return assets.RootElement.GetProperty("project").GetProperty("frameworks").GetProperty("net10.0").TryGetProperty("frameworkReferences", out JsonElement references)
            ? [.. references.EnumerateObject().Select(reference => reference.Name).Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>
    /// What a library's compiled assembly references, read from its metadata: the assembly names, and the types it takes
    /// from <c>Community.Zabbix.Api</c>. <paramref name="library"/> is the project's folder under <c>src</c>; its assembly
    /// is named after its package, <c>Community.</c> and the folder.
    /// </summary>
    private static (List<string> Assemblies, List<string> ApiTypes) References(string library)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Community." + library + ".dll");
        Assert.True(File.Exists(path), $"{path} is missing: the test project references every library");
        using FileStream file = File.OpenRead(path);
        using var image = new PEReader(file);
        MetadataReader metadata = image.GetMetadataReader();
        List<string> assemblies = [.. metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).Order(StringComparer.Ordinal)];
        List<string> apiTypes = [.. metadata.TypeReferences
            .Select(handle => metadata.GetTypeReference(handle))
            .Where(type => type.ResolutionScope.Kind == HandleKind.AssemblyReference
                && metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name) == "Community.Zabbix.Api")
            .Select(type => $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}")
            .Distinct()
            .Order(StringComparer.Ordinal)];
        return (assemblies, apiTypes);
    }

    private static JsonDocument Assets(string library)
    {
        string path = Path.Combine(Repository.Root, "src", library, "obj", "project.assets.json");
        Assert.True(File.Exists(path), $"{path} is missing: restore the solution first");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
