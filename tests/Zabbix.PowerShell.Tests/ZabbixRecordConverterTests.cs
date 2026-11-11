// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Globalization;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// <see cref="ZabbixRecordConverter"/>, registered per record type through the generated
/// <c>Community.Zabbix.types.ps1xml</c>, so a bare hashtable binds to an SDK record through PowerShell's OWN parameter binder
/// (a cast, or a cmdlet parameter declared as the record itself) with no cmdlet or attribute calling
/// <see cref="ZabbixJsonBinder"/>. Exercised the same way <c>GetSelectionOptionsTests</c> exercises <c>-Filter</c>:
/// a real runspace with the module imported through its manifest (<see cref="Shell"/>), so <c>Community.Zabbix.types.ps1xml</c>
/// is actually loaded, not just the C# type reflected over directly.
/// </summary>
public sealed class ZabbixRecordConverterTests
{
    [Fact]
    public void A_hashtable_casts_directly_to_a_request_type()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            $r = [Community.Zabbix.ZabbixHostGroupCreateRequest]@{ name = 'Databases' }
            $r.name
            """);

        Assert.Empty(run.Errors);
        Assert.Equal("Databases", Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public void A_hashtable_binds_directly_to_a_cmdlet_parameter_declared_as_the_record_itself()
    {
        // No cmdlet in this module happens to declare a bare TRequest-typed parameter (every generated New-/Set-
        // builds its own request from named parameters), so this proves the underlying claim — "PowerShell's OWN
        // binder converts a hashtable argument to a record-typed parameter with no ZabbixJsonBinder call in the
        // way" — directly against a plain function parameter, the same binder path a cmdlet parameter uses.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            function Test-ZbxRecordParameter {
                param([Community.Zabbix.ZabbixHostGroupCreateRequest]$Request)
                $Request.name
            }
            Test-ZbxRecordParameter -Request @{ name = 'Web servers' }
            """);

        Assert.Empty(run.Errors);
        Assert.Equal("Web servers", Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public void A_hashtable_binds_at_two_levels_of_nesting()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            $r = [Community.Zabbix.ZabbixDashboardCreateRequest]@{
                name = 'd'
                pages = @(@{ widgets = @(@{ type = 'problems'; fields = @(@{ type = 1; name = 'x'; value = 'y' }) }) })
            }
            "{0}|{1}|{2}" -f $r.pages.Count, $r.pages[0].widgets.Count, $r.pages[0].widgets[0].fields.Count
            """);

        Assert.Empty(run.Errors);
        Assert.Equal("1|1|1", Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public void A_PSCustomObject_binds_the_same_as_a_hashtable()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            $obj = [pscustomobject]@{ name = 'Databases' }
            $r = [Community.Zabbix.ZabbixHostGroupCreateRequest]$obj
            $r.name
            """);

        Assert.Empty(run.Errors);
        Assert.Equal("Databases", Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public void An_ordered_dictionary_binds_the_same_as_a_hashtable()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            $r = [Community.Zabbix.ZabbixHostGroupGetParameters]([ordered]@{ WithHosts = $true; with_graphs = $true })
            "{0}|{1}" -f $r.WithHosts, $r.WithGraphs
            """);

        Assert.Empty(run.Errors);
        Assert.Equal("True|True", Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public void A_PSCustomObject_has_its_CSharp_and_wire_named_keys_remapped_like_a_hashtable()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            $r = [Community.Zabbix.ZabbixHostGroupGetParameters]([pscustomobject]@{ WithHosts = $true; with_graphs = $true })
            "{0}|{1}" -f $r.WithHosts, $r.WithGraphs
            """);

        Assert.Empty(run.Errors);
        Assert.Equal("True|True", Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public void An_already_typed_record_nested_inside_a_hashtable_keeps_its_own_wire_names()
    {
        // A typed record VALUE inside a hashtable (ConfigurationCommandsTests' -Rules case) must not reach
        // ZabbixJsonShape.FromShell, whose ConvertTo-Json-shaped writer emits the C# property name (CreateMissing),
        // which the wire contract (createMissing) would silently drop.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            $r = [Community.Zabbix.ZabbixConfigurationImportRuleRequest]@{ Hosts = [Community.Zabbix.ZabbixConfigurationImportRuleHostRequest]@{ CreateMissing = $true } }
            $r.Hosts.CreateMissing
            """);

        Assert.Empty(run.Errors);
        Assert.Equal(true, Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public void A_single_hashtable_bound_to_a_record_array_target_is_one_element_not_enumerated_key_by_key()
    {
        // A bare Hashtable for a collection target: a Hashtable is itself IEnumerable (of DictionaryEntry) — @{ groupid = '2' } meant as ONE
        // group must not be misread as a sequence of DictionaryEntry values that match no record shape.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            $groups = [Community.Zabbix.ZabbixHostGroupRequest[]]@{ groupid = '2' }
            "{0}|{1}" -f $groups.Count, $groups[0].GroupId
            """);

        Assert.Empty(run.Errors);
        Assert.Equal("1|2", Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public void An_already_typed_value_passes_through_unchanged()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            $typed = [Community.Zabbix.ZabbixHostGroupCreateRequest]::new()
            $typed.name = 'Already typed'
            $r = [Community.Zabbix.ZabbixHostGroupCreateRequest]$typed
            [object]::ReferenceEquals($typed, $r)
            """);

        Assert.Empty(run.Errors);
        Assert.Equal(true, Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public void CanConvertTo_is_false_and_ConvertTo_throws_NotSupportedException()
    {
        var converter = new ZabbixRecordConverter();

        Assert.False(converter.CanConvertTo(new Hashtable(), typeof(Hashtable)));
        Assert.Throws<NotSupportedException>(() => converter.ConvertTo("x", typeof(Hashtable), CultureInfo.InvariantCulture, false));
    }

    [Fact]
    public void CanConvertFrom_is_false_for_a_type_the_JSON_context_does_not_know()
    {
        var converter = new ZabbixRecordConverter();

        // Not a string/primitive (those ARE registered, as JSON's own built-in scalars) — a type with no connection
        // to the SDK's JSON contract at all.
        Assert.False(converter.CanConvertFrom(new Hashtable(), typeof(ZabbixRecordConverterTests)));
    }

    [Fact]
    public void A_key_a_nested_record_does_not_model_is_an_error_naming_the_key_not_a_dropped_field()
    {
        // A nested-request record has no extension-data bag, so its JSON contract would
        // drop an unknown key without a word.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("[Community.Zabbix.ZabbixTagRequest]@{ tag = 'env'; bogus = 'x' }");

        System.Management.Automation.ErrorRecord error = Assert.Single(run.Errors);
        Assert.Contains("bogus", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_a_request_record_does_not_model_still_goes_to_its_AdditionalFields_bag()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("([Community.Zabbix.ZabbixHostGroupCreateRequest]@{ name = 'Databases'; newfield = 'x' }).AdditionalFields['newfield'].GetString()");

        Assert.Empty(run.Errors);
        Assert.Equal("x", Assert.Single(run.Output).BaseObject);
    }
}
