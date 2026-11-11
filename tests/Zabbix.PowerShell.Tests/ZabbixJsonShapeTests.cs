// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Collections.Specialized;
using System.Management.Automation;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// No wire JSON comes from PowerShell's Newtonsoft-based <c>JsonObject</c>
/// (<c>ConvertTo-Json</c>/<c>ConvertFrom-Json</c>). <see cref="ZabbixJsonShape"/> walks the PowerShell object graph
/// into System.Text.Json itself; a typed SDK record inside it goes through the SDK's own source-generated contract.
/// </summary>
public sealed partial class ZabbixJsonShapeTests
{
    [Fact]
    public void A_hashtable_with_a_nested_pscustomobject_and_a_typed_record_writes_what_the_STJ_contract_writes()
    {
        var rule = new ZabbixConfigurationImportRuleHostRequest { CreateMissing = true, UpdateExisting = false };
        var nested = new PSObject();
        nested.Properties.Add(new PSNoteProperty("a", 1));
        nested.Properties.Add(new PSNoteProperty("b", new object[] { "p", "q" }));
        var table = new OrderedDictionary { ["name"] = "x", ["nested"] = nested, ["rule"] = rule, ["none"] = null };

        JsonElement shaped = ZabbixJsonShape.FromShell(table);

        string contract = JsonSerializer.Serialize(rule, ZabbixApiJson.Default.ZabbixConfigurationImportRuleHostRequest);
        Assert.Equal("""{"name":"x","nested":{"a":1,"b":["p","q"]},"rule":""" + contract + ""","none":null}""", shaped.GetRawText());
    }

    [Fact]
    public void An_enum_is_its_number_and_an_instant_is_unix_seconds()
    {
        var table = new OrderedDictionary
        {
            ["status"] = Community.Zabbix.PowerShell.Commands.ZabbixConfigurationFormat.Yaml,
            ["since"] = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero),
            ["flag"] = true,
            ["ratio"] = 0.5,
        };

        JsonElement shaped = ZabbixJsonShape.FromShell(table);

        Assert.Equal("""{"status":2,"since":1790294400,"flag":true,"ratio":0.5}""", shaped.GetRawText());
    }

    [Fact]
    public void A_SecureString_is_refused_rather_than_written()
    {
        using var secret = new SecureString();
        secret.AppendChar('x');

        ArgumentException refused = Assert.Throws<ArgumentException>(() => ZabbixJsonShape.FromShell(new Hashtable { ["password"] = secret }));

        Assert.Contains("SecureString", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_string_at_the_top_is_the_JSON_text_it_holds_and_nested_it_is_a_string()
    {
        Assert.Equal("""{"output":"extend"}""", ZabbixJsonShape.FromShell("""{"output":"extend"}""").GetRawText());
        Assert.Equal("""{"output":"extend"}""", ZabbixJsonShape.FromShell(new Hashtable { ["output"] = "extend" }).GetRawText());
    }

    [Fact]
    public void ToShell_shapes_a_big_integer_and_a_float_as_ConvertFrom_Json_does()
    {
        using JsonDocument document = JsonDocument.Parse("""[18446744073709551615, 1.5, "2026-09-25T00:00:00Z"]""");

        object[] shaped = Assert.IsType<object[]>(ZabbixJsonShape.ToShell(document.RootElement));

        Assert.Equal(System.Numerics.BigInteger.Parse("18446744073709551615", System.Globalization.CultureInfo.InvariantCulture), shaped[0]);
        Assert.Equal(1.5, shaped[1]);
        // A deliberate difference from ConvertFrom-Json (which turns an ISO-looking string into a DateTime): a
        // string the server sent stays the string it sent.
        Assert.Equal("2026-09-25T00:00:00Z", shaped[2]);
    }

    [Fact]
    public void No_module_source_calls_PowerShells_Newtonsoft_based_JsonObject()
    {
        string root = ModuleSourceRoot();

        string[] offenders = [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadLines(path).Any(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && NewtonsoftJsonObject().IsMatch(line)))
            .Select(Path.GetFileName)!];

        Assert.Empty(offenders);
    }

    // JsonObject.ConvertToJson/ConvertFromJson/ConvertToJsonContext (Microsoft.PowerShell.Commands), not
    // System.Text.Json.Nodes.JsonObject: the call names are unique to PowerShell's type. Comment lines (which may name it to say why it is
    // not used) do not count.
    [GeneratedRegex(@"ConvertToJson\b|ConvertFromJson\b|ConvertToJsonContext\b|Microsoft\.PowerShell\.Commands\.JsonObject", RegexOptions.CultureInvariant)]
    private static partial Regex NewtonsoftJsonObject();

    private static string ModuleSourceRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "src", "Zabbix.PowerShell");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("src/Zabbix.PowerShell above " + AppContext.BaseDirectory);
    }
}
