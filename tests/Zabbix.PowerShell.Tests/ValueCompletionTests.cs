// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Reflection;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// Tab completes the values a parameter takes from a known set, not file names: <c>-SortField</c> the fields the
/// <c>.get</c> sorts by (the description's <c>sortable</c>, the SDK's <c>SortableFields</c>), <c>Invoke-ZbxApi -Method</c>
/// the API's methods the connected server's version has.
/// </summary>
public sealed class ValueCompletionTests
{
    /// <summary>Every <c>Get-</c> on <see cref="ZabbixGetCmdlet{TEntity, TParameters}"/>, generated or hand-written.</summary>
    private static readonly string[] s_gets = [.. ModuleUnderTest.Zabbix.Cmdlets
        .Where(type => ParametersOf(type) is not null)
        .Select(type => type.GetCustomAttribute<CmdletAttribute>()!)
        .Select(cmdlet => $"{cmdlet.VerbName}-{cmdlet.NounName}")
        .Order(StringComparer.Ordinal)];

    private static Type? ParametersOf(Type type)
    {
        for (Type? t = type.BaseType; t is not null; t = t.BaseType)
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ZabbixGetCmdlet<,>)) return t.GetGenericArguments()[1];
        return null;
    }

    [Fact]
    public void SortField_completes_exactly_the_fields_each_get_sorts_by()
    {
        using Shell shell = Shell.Open();
        List<string> violations = [];
        foreach (string command in s_gets)
        {
            Type type = ModuleUnderTest.Zabbix.Cmdlets.Single(t => t.GetCustomAttribute<CmdletAttribute>() is { } c && $"{c.VerbName}-{c.NounName}" == command);
            var sortable = (IEnumerable<string>)ParametersOf(type)!.GetProperty("SortableFields", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            Run run = shell.Invoke("(TabExpansion2 -inputScript $s -cursorColumn $s.Length).CompletionMatches.CompletionText", new Dictionary<string, object?> { ["s"] = command + " -SortField " });
            string[] offered = [.. run.Output.Where(o => o is not null).Select(o => o.ToString()!).Order(StringComparer.Ordinal)];
            string[] expected = [.. sortable.Order(StringComparer.Ordinal)];
            if (expected.Length > 0 && !offered.SequenceEqual(expected)) violations.Add($"{command}: offers {string.Join(",", offered)}, sorts by {string.Join(",", expected)}");
        }

        Assert.True(violations.Count == 0, string.Join('\n', violations));
    }

    [Fact]
    public void Tab_after_SortField_offers_the_object_s_fields()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("$s = 'Get-ZbxItem -SortField '; (TabExpansion2 -inputScript $s -cursorColumn $s.Length).CompletionMatches.CompletionText");

        Assert.Empty(run.Errors);
        string[] offered = [.. run.Output.Where(o => o is not null).Select(o => o.ToString())];
        Assert.Contains("key_", offered);
        Assert.Contains("name", offered);
        Assert.DoesNotContain(offered, text => text.Contains('/', StringComparison.Ordinal) || text.StartsWith("./", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tab_after_Method_offers_the_methods_the_connected_version_has()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("foreach ($s in 'Invoke-ZbxApi -Method host.', 'Invoke-ZbxApi apm', 'Invoke-ZbxApi -Method discoveryrule.c') { (TabExpansion2 -inputScript $s -cursorColumn $s.Length).CompletionMatches.CompletionText; '--' }");

        Assert.Empty(run.Errors);
        string[] offered = [.. run.Output.Where(o => o is not null).Select(o => o.ToString())];
        int first = Array.IndexOf(offered, "--");
        Assert.Contains("host.get", offered[..first]);
        Assert.Contains("host.create", offered[..first]);
        Assert.All(offered[..first], method => Assert.StartsWith("host.", method, StringComparison.Ordinal));
        // The fake server is 7.0: apm*.get arrived in 8.0, discoveryrule.copy is there until 7.4.
        int second = Array.IndexOf(offered, "--", first + 1);
        Assert.Empty(offered[(first + 1)..second]);
        Assert.Contains("discoveryrule.copy", offered[(second + 1)..^1]);
    }

    [Fact]
    public void Without_a_connection_Method_completes_every_described_method()
    {
        Assert.Contains("apmlog.get", ZabbixApiMethodCompleterAttribute.Methods("apm", version: null));
        Assert.DoesNotContain("apmlog.get", ZabbixApiMethodCompleterAttribute.Methods("apm", new Version(7, 0)));
        Assert.Equal(ZabbixApiMethods.All.Count, ZabbixApiMethodCompleterAttribute.Methods(string.Empty, version: null).Count());
    }
}
