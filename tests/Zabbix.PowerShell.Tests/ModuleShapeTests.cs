// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;
using System.Reflection;
using System.Security;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>Structural invariants of the module, over every compiled cmdlet: a test, not prose, holds each one.</summary>
public sealed partial class ModuleShapeTests
{
    private static readonly Type[] Cmdlets = [.. ModuleUnderTest.AllCmdlets];

    [Theory]
    [MemberData(nameof(ModuleUnderTest.Names), MemberType = typeof(ModuleUnderTest))]
    public void The_manifest_exports_exactly_the_compiled_cmdlets(string module)
    {
        ModuleUnderTest under = ModuleUnderTest.Named(module);
        Hashtable manifest = ReadManifest(under.Manifest);
        Type[] cmdlets = [.. under.Cmdlets];

        string[] compiled = [.. cmdlets.Select(type => type.GetCustomAttribute<CmdletAttribute>()!).Select(c => $"{c.VerbName}-{c.NounName}").Order(StringComparer.Ordinal)];
        string[] aliases = [.. cmdlets.SelectMany(type => type.GetCustomAttribute<AliasAttribute>()?.AliasNames ?? []).Order(StringComparer.Ordinal)];

        Assert.NotEmpty(compiled);
        Assert.Equal(compiled, Strings(manifest["CmdletsToExport"]).Order(StringComparer.Ordinal));
        Assert.Equal(aliases, Strings(manifest["AliasesToExport"]).Order(StringComparer.Ordinal));
        Assert.Equal(Path.GetFileName(under.Assembly.Location), manifest["RootModule"]);
        Assert.Empty(Strings(manifest["FunctionsToExport"]));
        Assert.Empty(Strings(manifest["VariablesToExport"]));
    }

    [Fact]
    public void The_three_modules_split_the_commands_between_them()
    {
        // ADR 0003: every cmdlet in exactly one module; the agent and proxy commands are not in Zabbix.
        string[] all = [.. Cmdlets.Select(type => type.GetCustomAttribute<CmdletAttribute>()!).Select(c => $"{c.VerbName}-{c.NounName}")];

        Assert.Equal(all.Length, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal((267, 20, 23), (ModuleUnderTest.Zabbix.Cmdlets.Count(), ModuleUnderTest.Agent.Cmdlets.Count(), ModuleUnderTest.Proxy.Cmdlets.Count()));
    }

    [Fact]
    public void The_readmes_count_the_cmdlets_each_module_exports()
    {
        // The numbers a reader sees first: the module README's table and total, its reference-page count, and the
        // feature list's headings.
        int[] counts = [ModuleUnderTest.Zabbix.Cmdlets.Count(), ModuleUnderTest.Agent.Cmdlets.Count(), ModuleUnderTest.Proxy.Cmdlets.Count()];
        string module = File.ReadAllText(Path.Combine(ModuleUnderTest.RepositoryRoot, "docs", "design", "powershell-module.md"));
        string features = File.ReadAllText(Path.Combine(ModuleUnderTest.RepositoryRoot, "docs", "FEATURES.md"));
        string[] names = ["Community.Zabbix", "Community.Zabbix.Agent", "Community.Zabbix.Proxy"];

        int[] moduleTable = [.. names.Select(name => int.Parse(Regex.Match(module, $@"\| `{Regex.Escape(name)}` \|[^|\n]*\| (\d+) \|").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];
        int[] featureHeadings = [.. names.Select(name => int.Parse(Regex.Match(features, $@"### `{Regex.Escape(name)}` \((\d+) cmdlets\)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];
        int total = int.Parse(Regex.Match(module, @"the three export (\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Match pages = Regex.Match(module, @"\((\d+), (\d+) and (\d+) pages");

        Assert.Equal(counts, moduleTable);
        Assert.Equal(counts, featureHeadings);
        Assert.Equal(counts.Sum(), total);
        int[] pageCounts = [.. pages.Groups.Values.Skip(1).Select(group => int.Parse(group.Value, System.Globalization.CultureInfo.InvariantCulture))];
        Assert.Equal(counts, pageCounts);
    }

    [Theory]
    [MemberData(nameof(ModuleUnderTest.Names), MemberType = typeof(ModuleUnderTest))]
    public void Every_parameter_alias_binds_what_a_name_cannot(string module)
    {
        // An alias is permanent API from 1.0 on, so only the ones that do work are kept (docs/design/powershell-binding.md,
        // "Parameter names and aliases"): -Id on an -InputObject, a piped object's own <Concept>Id property (Get-ZbxHost |
        // Get-ZbxItem binds HostId), the spelling of a name that would hide a PSCmdlet member (-Host), the name host-like
        // objects share for one field (-Name, -DisplayName), and PowerShell's own path and computer aliases. Never a
        // former spelling: a plural id list, a *Raw twin, a mechanical capitalisation.
        string[] shared = ["Id", "Host", "Name", "DisplayName", "ComputerName", "PSPath", "LP", "Path", "FullName", "LiteralPath"];
        var pipelineId = new Regex("^[A-Z][A-Za-z0-9]*Id$");
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            $common = [System.Management.Automation.Cmdlet]::CommonParameters + [System.Management.Automation.Cmdlet]::OptionalCommonParameters
            foreach ($c in Get-Command -Module $module -CommandType Cmdlet) {
                foreach ($p in $c.Parameters.Values) { if ($p.Name -notin $common) { foreach ($a in $p.Aliases) { "$($c.Name) -$($p.Name) -$a" } } }
            }
            """, new Dictionary<string, object?> { ["module"] = module });

        Assert.Empty(run.Errors);
        string[] former = [.. run.Output.Select(o => o.ToString()).Where(line =>
        {
            string alias = line[(line.LastIndexOf(" -", StringComparison.Ordinal) + 2)..];
            return !shared.Contains(alias, StringComparer.Ordinal) && !pipelineId.IsMatch(alias);
        })];
        Assert.Empty(former);
    }

    [Theory]
    [MemberData(nameof(ModuleUnderTest.Names), MemberType = typeof(ModuleUnderTest))]
    public void Importing_the_manifest_exposes_exactly_the_compiled_cmdlets_and_their_aliases(string module)
    {
        using Shell shell = Shell.Open();
        Type[] cmdlets = [.. ModuleUnderTest.Named(module).Cmdlets];

        Run run = shell.Invoke("Get-Command -Module $module | ForEach-Object Name", new Dictionary<string, object?> { ["module"] = module });

        string[] expected = [.. cmdlets.Select(type => type.GetCustomAttribute<CmdletAttribute>()!).Select(c => $"{c.VerbName}-{c.NounName}")
            .Concat(cmdlets.SelectMany(type => type.GetCustomAttribute<AliasAttribute>()?.AliasNames ?? [])).Order(StringComparer.Ordinal)];
        Assert.Equal(expected, run.Output.Select(o => (string)o.BaseObject).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ModuleUnderTest.Names), MemberType = typeof(ModuleUnderTest))]
    public void Every_manifest_carries_the_package_version_and_a_sibling_requires_Zabbix(string module)
    {
        // ADR 0003 and tools/Test-ZbxReleaseVersion.ps1: ModuleVersion is Directory.Build.props' VersionPrefix, a prerelease
        // carries its VersionSuffix without the dots as PSData.Prerelease (alpha.1 is alpha1) and a release carries none,
        // no command is an experimental feature, and Community.Zabbix.Agent/Community.Zabbix.Proxy load Community.Zabbix first.
        Hashtable manifest = ReadManifest(ModuleUnderTest.Named(module).Manifest);
        Hashtable data = (Hashtable)((Hashtable)manifest["PrivateData"]!)["PSData"]!;
        string props = File.ReadAllText(Path.Combine(ModuleUnderTest.RepositoryRoot, "Directory.Build.props"));
        string prefix = Regex.Match(props, "<VersionPrefix>([^<]*)</VersionPrefix>").Groups[1].Value.Trim();
        string suffix = Regex.Match(props, "<VersionSuffix>([^<]*)</VersionSuffix>").Groups[1].Value.Trim();

        Assert.NotEmpty(prefix);
        Assert.Equal(prefix, manifest["ModuleVersion"]);
        if (suffix.Length == 0) Assert.False(data.ContainsKey("Prerelease"));
        else Assert.Equal(suffix.Replace(".", "", StringComparison.Ordinal), data["Prerelease"]);
        Assert.False(data.ContainsKey("ExperimentalFeatures"));
        Assert.Equal("https://github.com/schroeder247/community-zabbix", data["ProjectUri"]);
        if (module == "Community.Zabbix")
        {
            Assert.False(manifest.ContainsKey("RequiredModules"));
            return;
        }
        // Pinned to the family's version: a sibling compiles against Community.Zabbix's internals, so it loads only beside
        // the copy it was built with (the build writes the pin; the template names the module). A prerelease names the
        // module only, because RequiredVersion is a System.Version and has no place for a prerelease label.
        object required = Assert.Single(((IEnumerable)manifest["RequiredModules"]!).Cast<object>());
        if (suffix.Length > 0)
        {
            Assert.Equal("Community.Zabbix", required);
            return;
        }
        Hashtable pin = Assert.IsType<Hashtable>(required);
        Assert.Equal("Community.Zabbix", pin["ModuleName"]);
        Assert.Equal(manifest["ModuleVersion"], pin["RequiredVersion"]);
    }

    [Fact]
    public void No_command_or_parameter_is_an_experimental_feature()
    {
        // ADR 0003: no command is hidden behind Enable-ExperimentalFeature.
        string[] experimental = [.. Cmdlets
            .Where(type => type.GetCustomAttributes<ExperimentalAttribute>().Any()
                || type.GetProperties().Any(property => property.GetCustomAttributes<ExperimentalAttribute>().Any()))
            .Select(type => type.Name)];

        Assert.Empty(experimental);
    }

    [Fact]
    public void The_module_Zabbix_carries_no_host_listener_or_database_assembly()
    {
        // The module must not carry EF Core, Npgsql, SQLite, Kestrel or the hosts to every user. It
        // speaks two protocols, the API (Community.Zabbix.Sdk) and the framed wire (Community.Zabbix.Wire, with BouncyCastle for PSK), and
        // carries nothing of the hosting packages or of the dependencies ADR 0006 removed (service discovery, YAML).
        string[] forbidden = ["Community.Zabbix.Agent.dll", "Community.Zabbix.Proxy.dll", "Community.Zabbix.Agent.Storage.EntityFrameworkCore.dll", "Microsoft.EntityFrameworkCore", "Npgsql",
            "SQLitePCLRaw", "Microsoft.Data.Sqlite", "Microsoft.AspNetCore.", "Microsoft.Extensions.Diagnostics.HealthChecks",
            "Microsoft.Extensions.ServiceDiscovery", "YamlDotNet", "Zabbix.Configuration.Yaml"];
        string[] files = [.. Directory.EnumerateFiles(ModuleUnderTest.Zabbix.BuildDirectory, "*", SearchOption.AllDirectories).Select(Path.GetFileName)!];

        Assert.Contains("Community.Zabbix.Sdk.dll", files);
        Assert.Contains("Community.Zabbix.Wire.dll", files);
        Assert.Contains("BouncyCastle.Cryptography.dll", files);
        Assert.DoesNotContain(files, file => forbidden.Any(name => file.StartsWith(name, StringComparison.Ordinal)));
    }

    [Fact]
    public void A_secret_is_never_a_positional_string()
    {
        var parameters = Cmdlets
            .SelectMany(type => type.GetProperties().Select(property => (Type: type, Property: property, Attribute: property.GetCustomAttributes<ParameterAttribute>().ToArray())))
            .Where(parameter => parameter.Attribute.Length > 0)
            .ToArray();

        Assert.NotEmpty(parameters);
        foreach ((Type type, PropertyInfo property, ParameterAttribute[] attributes) in parameters)
        {
            bool secretType = property.PropertyType == typeof(SecureString) || property.PropertyType == typeof(PSCredential);
            bool secretName = SecretName().IsMatch(property.Name);
            Assert.False(secretName && !secretType, $"{type.Name}.{property.Name} carries a secret as {property.PropertyType.Name}; use SecureString or PSCredential");
            Assert.False(secretType && attributes.Any(attribute => attribute.Position != int.MinValue), $"{type.Name}.{property.Name} is a positional secret");
        }
        Assert.Contains(parameters, parameter => parameter.Property.PropertyType == typeof(SecureString));
    }

    [Fact]
    public void Every_ids_option_a_generated_Gets_get_parameters_carry_has_a_filter_parameter()
    {
        // A generated Get-Zbx<Entity>'s own *GetParameters record carries a *ids option (item.get's hostids, groupids,
        // ...) for every entity it can be filtered by, and every such option has a parameter of its own, not only
        // -Filter (the whole typed record): a ZabbixReference<TEntity>[] (resolved by name in one .get) when the option names one fixed
        // entity type, else a plain string[]? passthrough (ZabbixCmdletEmitter.ReferenceConceptObject explains each
        // case either way, in the emitter). The entity's own id option is exempt: its plural already equals the
        // wire field -Id itself binds.
        MethodInfo ofMethod = typeof(ZabbixEntities).GetMethod(nameof(ZabbixEntities.Of))!;
        Type[] generatedGetCommands = [.. Cmdlets.Where(type => type.Namespace == "Community.Zabbix.PowerShell.Commands.Generated"
            && type.BaseType is { IsGenericType: true } baseType && baseType.GetGenericTypeDefinition() == typeof(ZabbixGetCmdlet<,>))];
        Assert.NotEmpty(generatedGetCommands);

        foreach (Type command in generatedGetCommands)
        {
            Type[] typeArguments = command.BaseType!.GetGenericArguments();
            object shape = ofMethod.MakeGenericMethod(typeArguments[0]).Invoke(null, null)!;
            string idField = (string)shape.GetType().GetProperty("IdField")!.GetValue(shape)!;

            HashSet<string> reachable = [.. command.GetProperties()
                .Where(property => property.GetCustomAttributes<ParameterAttribute>().Any())
                .SelectMany(property => new[] { property.Name }.Concat(property.GetCustomAttribute<AliasAttribute>()?.AliasNames ?? []))];

            foreach (PropertyInfo idsProperty in typeArguments[1].GetProperties())
            {
                string? wire = idsProperty.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
                if (wire is null || idsProperty.PropertyType != typeof(string[]) || !wire.EndsWith("ids", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(wire, idField + "s", StringComparison.Ordinal)) continue;

                Assert.True(reachable.Contains(idsProperty.Name),
                    $"{command.Name}: {typeArguments[1].Name}.{idsProperty.Name} (wire '{wire}') has no filter parameter or alias reaching it");
            }
        }
    }

    [Fact]
    public void Every_cmdlet_derives_the_bridge_and_names_its_verb_by_constant()
    {
        string[] approved = [.. typeof(VerbsCommon).GetFields().Concat(typeof(VerbsCommunications).GetFields()).Concat(typeof(VerbsData).GetFields())
            .Concat(typeof(VerbsLifecycle).GetFields()).Concat(typeof(VerbsDiagnostic).GetFields()).Concat(typeof(VerbsSecurity).GetFields())
            .Concat(typeof(VerbsOther).GetFields()).Select(field => (string)field.GetValue(null)!)];

        Assert.All(Cmdlets, type => Assert.True(typeof(ZabbixCmdlet).IsAssignableFrom(type), $"{type.Name} does not derive ZabbixCmdlet"));
        Assert.All(Cmdlets, type => Assert.Contains(type.GetCustomAttribute<CmdletAttribute>()!.VerbName, approved));
        Assert.All(Cmdlets, type => Assert.StartsWith("Zbx", type.GetCustomAttribute<CmdletAttribute>()!.NounName, StringComparison.Ordinal));
    }

    private static Hashtable ReadManifest(string path)
    {
        using var shell = System.Management.Automation.PowerShell.Create();
        shell.AddCommand("Import-PowerShellDataFile").AddParameter("Path", path);
        return (Hashtable)Shell.Invoke(shell, "Import-PowerShellDataFile " + path).Single().BaseObject;
    }

    private static IEnumerable<string> Strings(object? value) => value switch
    {
        string one => [one],
        IEnumerable many => many.Cast<object>().Select(item => item is PSObject wrapped ? (string)wrapped.BaseObject : (string)item),
        _ => [],
    };

    // A name that ends in what it holds, either Zabbix spelling (passwd); TlsPskIdentity (a public label) and TokenUrl (a
    // URL) are not secrets (ZabbixCmdletEmitter.SecretName is the same pattern).
    [GeneratedRegex(@"(password|passwd|passphrase|secret|apikey|psk|token)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretName();

    [Fact]
    public void No_parameter_hides_a_member_of_PSCmdlet()
    {
        // A generated -Host declared `public new … Host` would hide PSCmdlet.Host (the PowerShell host); the
        // property takes its own name (LdapHost, HostReference) and -Host is its alias.
        string[] inherited = [.. typeof(PSCmdlet).GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(member => member.DeclaringType != typeof(object)).Select(member => member.Name)];
        string[] offenders = [.. CmdletTypes()
            .SelectMany(type => type.GetProperties().Where(property => property.GetCustomAttributes<ParameterAttribute>().Any() && inherited.Contains(property.Name))
                .Select(property => $"{type.Name}.{property.Name}"))];

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_parameter_has_an_ordinary_setter()
    {
        // A parameter declared { get; init; } binds (PowerShell sets it by reflection) but says it cannot be
        // set after construction, which a cmdlet's parameters are; and a test or a proxy command cannot set it.
        string[] offenders = [.. CmdletTypes()
            .SelectMany(type => type.GetProperties().Where(property => property.GetCustomAttributes<ParameterAttribute>().Any())
                .Where(property => property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) != false)
                .Select(property => $"{type.Name}.{property.Name}"))
            .Distinct()];

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_cmdlet_declares_a_void_OutputType()
    {
        // [OutputType(typeof(void))] told Get-Command a sink writes System.Void; a command
        // that writes nothing declares nothing (as PowerShell's own sinks do).
        string[] offenders = [.. CmdletTypes()
            .Where(t => t.GetCustomAttributes<OutputTypeAttribute>(true).SelectMany(o => o.Type).Any(n => n.Type == typeof(void)))
            .Select(t => t.Name)];

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_OutputType_names_what_is_written()
    {
        // OutputType is what Get-Command, tab completion and the help say a command writes: a bare PSObject or object
        // says nothing. A shaped answer names its ETS type (Zabbix.PowerShell.ServerInfo); only Invoke-ZbxApi, whose
        // answer is whatever the called method returns, writes untyped objects.
        string[] offenders = [.. ModuleUnderTest.AllCmdlets
            .Where(type => type.Name != "InvokeZbxApiCommand")
            .SelectMany(type => type.GetCustomAttributes<OutputTypeAttribute>(true).SelectMany(o => o.Type)
                .Where(name => name.Type == typeof(object) || name.Type == typeof(PSObject) || name.Type == typeof(PSCustomObject))
                .Select(name => $"{type.Name}: {name.Name}"))];

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_exception_the_modules_export_is_a_designed_type()
    {
        // The libraries' rule (DesignConformanceTests.Exceptions_are_designed_types) over the three module assemblies:
        // derived from the type callers already catch, never Exception itself; an error.type of its own; sealed, with the
        // three standard constructors.
        Type[] general = [typeof(Exception), typeof(SystemException), typeof(ApplicationException)];
        string[] offenders = [.. ModuleUnderTest.All.SelectMany(module => module.Assembly.GetExportedTypes())
            .Where(type => typeof(Exception).IsAssignableFrom(type))
            .SelectMany(type => new[]
            {
                general.Contains(type.BaseType) ? $"{type.Name}: derives from {type.BaseType!.Name}" : null,
                type.GetInterfaces().Any(contract => contract.FullName == "Community.Zabbix.Diagnostics.IZabbixErrorTyped") ? null : $"{type.Name}: names no error.type",
                type.IsSealed ? null : $"{type.Name}: not sealed",
                type.GetConstructor(Type.EmptyTypes) is null || type.GetConstructor([typeof(string)]) is null || type.GetConstructor([typeof(string), typeof(Exception)]) is null
                    ? $"{type.Name}: lacks a standard constructor" : null,
            })
            .OfType<string>()];

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_array_or_settable_collection_property_is_a_parameter()
    {
        // The modules suppress CA1819 (array properties) and CA2227 (settable collections) for one reason: the
        // parameter binder sets a multi-valued parameter, so it is an array or a hashtable with a setter. This keeps
        // the suppression honest: no other public property in the three assemblies is shaped so.
        string[] offenders = [.. ModuleUnderTest.All.SelectMany(module => module.Assembly.GetExportedTypes())
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(property => (Type: type, Property: property)))
            .Where(member => (member.Property.PropertyType.IsArray
                    || (member.Property.SetMethod is { IsPublic: true } setter && !setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit))
                        && typeof(System.Collections.IEnumerable).IsAssignableFrom(member.Property.PropertyType) && member.Property.PropertyType != typeof(string)))
                && !member.Property.GetCustomAttributes<ParameterAttribute>(true).Any()
                // An entity without names still overrides the base's abstract -Name (unbound: no [Parameter]).
                && member.Property.GetMethod?.GetBaseDefinition().IsAbstract != true)
            .Select(member => $"{member.Type.Name}.{member.Property.Name}")];

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_Remove_is_high_impact()
    {
        // One impact for one verb: a Remove- asks at the default $ConfirmPreference, a generated delete or a hand-written
        // membership, tag or link removal alike (-Force or -Confirm:$false runs it unprompted).
        string[] offenders = [.. CmdletTypes()
            .Select(t => (Type: t, Cmdlet: t.GetCustomAttribute<CmdletAttribute>()!))
            .Where(c => c.Cmdlet.VerbName == VerbsCommon.Remove && c.Cmdlet.ConfirmImpact != ConfirmImpact.High)
            .Select(c => c.Type.Name)];

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_OutputType_is_a_public_type()
    {
        // Get-Command's OutputType, Get-Member and a format view name the type a command writes: an internal one is a
        // type a script cannot name in a [OutputType] of its own, a cast or a -is test.
        string[] offenders = [.. CmdletTypes()
            .SelectMany(t => t.GetCustomAttributes<OutputTypeAttribute>(true).SelectMany(o => o.Type).Select(n => (Cmdlet: t, n.Type)))
            .Where(output => output.Type is { IsVisible: false })
            .Select(output => $"{output.Cmdlet.Name}: {output.Type!.FullName}")];

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_cmdlet_with_PassThru_declares_what_it_passes_through()
    {
        string[] offenders = [.. CmdletTypes()
            .Where(t => t.GetProperty("PassThru") is not null && !t.GetCustomAttributes<OutputTypeAttribute>(true).Any())
            .Select(t => t.Name)];

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_high_impact_cmdlet_has_Force()
    {
        // Learn RD05: a command that prompts by default takes -Force so a script or a host that cannot prompt can run it.
        string[] offenders = [.. CmdletTypes()
            .Where(t => t.GetCustomAttribute<CmdletAttribute>()!.ConfirmImpact == ConfirmImpact.High && t.GetProperty("Force") is null)
            .Select(t => t.Name)];

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("Set")]
    [InlineData("Remove")]
    [InlineData("Clear")]
    [InlineData("Add")]
    [InlineData("Send")]
    [InlineData("Publish")]
    [InlineData("Import")]
    [InlineData("Copy")]
    [InlineData("Enable")]
    [InlineData("Disable")]
    [InlineData("Reset")]
    [InlineData("Unblock")]
    [InlineData("Sync")]
    [InlineData("Start")]
    [InlineData("Stop")]
    [InlineData("Unregister")]
    public void Every_state_changing_verb_supports_ShouldProcess(string verb)
    {
        // Learn RD04. New- (builders that change nothing are in-memory, New-Object's precedent), Register- (a handler
        // registered in this runspace's own session) and Initialize-/Invoke- reads are decided per command.
        string[] offenders = [.. CmdletTypes()
            .Select(t => (Type: t, Cmdlet: t.GetCustomAttribute<CmdletAttribute>()!))
            .Where(c => c.Cmdlet.VerbName == verb && !c.Cmdlet.SupportsShouldProcess)
            .Select(c => c.Type.Name)];

        Assert.Empty(offenders);
    }

    private static IEnumerable<Type> CmdletTypes() => ModuleUnderTest.AllCmdlets;
}
