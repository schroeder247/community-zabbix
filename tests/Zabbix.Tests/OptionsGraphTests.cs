// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix.Tests;

/// <summary>
/// The options graph is one composition root — every named <c>Add*</c> verb across the SDK's
/// libraries writes a row a document section reaches (<see cref="ZabbixConfigurationSections"/>, <see cref="ZabbixProxiesServiceCollectionExtensions"/>),
/// or says why no section does, and every secret-shaped member on a section is classified so it never renders.
/// </summary>
public sealed class OptionsGraphTests
{
    private static readonly IReadOnlyList<Assembly> Libraries = Conformance.Libraries.All;

    /// <summary>
    /// A named verb — <c>Add*(this IServiceCollection, string name, …)</c> — is exactly a row in the graph: the name is
    /// the options name, the configuration key, the keyed service key. Every one this reflection finds must be a key of
    /// this table, and the table names the section (<see cref="ZabbixConfigurationSections"/> or <see cref="ZabbixProxiesServiceCollectionExtensions"/>)
    /// that binds it from a document. Add a row here with a new named verb, or this test explains why not.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DocumentSectionOf = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AddZabbix"] = ZabbixConfigurationSections.Servers,                    // Zabbix
        ["AddZabbixPushAgent"] = ZabbixConfigurationSections.PushAgents,                // Zabbix
        ["AddZabbixActiveAgent"] = ZabbixConfigurationSections.ActiveAgents,    // Zabbix
        ["AddZabbixPassiveAgent"] = ZabbixConfigurationSections.PassiveAgents,  // Zabbix.Proxy
        ["AddZabbixActiveProxy"] = ZabbixConfigurationSections.Proxies,               // Zabbix.Proxy, ZabbixProxyOptions.Mode == Active
        ["AddZabbixPassiveProxy"] = ZabbixConfigurationSections.Proxies,        // Zabbix.Proxy, ZabbixProxyOptions.Mode == Passive
    };

    /// <summary>
    /// The named verbs that write the graph with no section of the document, each with why. Their options still bind from
    /// configuration, through the builder's own <c>Bind(section)</c>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> WithoutADocumentSection = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AddZabbixWireClient"] = "The document's servers are the API's; a wire client binds the section its registration names.",   // Zabbix.Wire
    };

    private static IEnumerable<MethodInfo> NamedAddExtensions()
        => Libraries.SelectMany(library => library.GetExportedTypes())
            .Where(type => type.IsAbstract && type.IsSealed)                                  // static classes
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.IsDefined(typeof(ExtensionAttribute), false)
                             && method.Name.StartsWith("Add", StringComparison.Ordinal)
                             && method.GetParameters() is [{ ParameterType: var receiver }, { ParameterType: var second }, ..]
                             && receiver == typeof(IServiceCollection) && second == typeof(string));

    [Fact]
    public void Every_add_extension_writes_the_options_graph()
    {
        string[] found = [.. NamedAddExtensions().Select(method => method.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] documented = [.. DocumentSectionOf.Keys.Concat(WithoutADocumentSection.Keys).Order(StringComparer.Ordinal)];

        Assert.Equal(documented, found);
    }

    /// <summary>Every section this table names actually exists as a constant on the document that is supposed to bind it.</summary>
    [Theory]
    [InlineData(ZabbixConfigurationSections.Servers)]
    [InlineData(ZabbixConfigurationSections.PushAgents)]
    [InlineData(ZabbixConfigurationSections.ActiveAgents)]
    [InlineData(ZabbixConfigurationSections.PassiveAgents)]
    [InlineData(ZabbixConfigurationSections.Proxies)]
    public void Every_documented_section_is_a_real_key(string section) => Assert.False(string.IsNullOrWhiteSpace(section));

    /// <summary>
    /// Every options type a named verb writes: a member whose name says it carries a credential is classified
    /// (<see cref="ZabbixSecretAttribute"/>) — the rule that keeps it out of a log applies uniformly across
    /// every section, not only <see cref="ZabbixOptions.ApiToken"/>.
    /// </summary>
    [Fact]
    public void A_secret_in_a_document_binds_as_classified()
    {
        Type[] sections = [typeof(ZabbixOptions), typeof(ZabbixPushAgentOptions), typeof(ZabbixActiveAgentOptions), typeof(ZabbixPassiveAgentOptions), typeof(ZabbixProxyOptions), typeof(ZabbixListenerOptions), typeof(ZabbixWireOptions)];
        string[] secretShaped = ["Token", "Psk", "Password", "PreSharedKey", "PreSharedKeys", "Secret"];

        List<string> unclassified = [];
        foreach (Type section in sections)
        {
            foreach (PropertyInfo property in section.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                bool looksSecret = secretShaped.Any(word => property.Name.Contains(word, StringComparison.Ordinal));
                bool isString = property.PropertyType == typeof(string) || property.PropertyType == typeof(IDictionary<string, string>);
                if (looksSecret && isString && !property.IsDefined(typeof(ZabbixSecretAttribute), inherit: true))
                    unclassified.Add($"{section.Name}.{property.Name}");
            }
        }

        Assert.Empty(unclassified);
    }

    /// <summary>The known secret-bearing members stay classified: a locked list, not just the pattern above.</summary>
    [Theory]
    [InlineData(typeof(ZabbixOptions), nameof(ZabbixOptions.ApiToken))]
    [InlineData(typeof(ZabbixListenerOptions), nameof(ZabbixListenerOptions.PreSharedKeys))]
    public void A_known_secret_member_is_classified(Type section, string property)
        => Assert.True(section.GetProperty(property)!.IsDefined(typeof(ZabbixSecretAttribute), inherit: true), $"{section.Name}.{property} lost its [ZabbixSecret] classification");
}
