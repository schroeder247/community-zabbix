// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using Community.Zabbix.Tests.Conformance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>
/// A method of the API is a <see cref="ZabbixApiMethod"/>, as an HTTP method is an <see cref="HttpMethod"/>: a property for
/// every method the description knows, the constructor and <see cref="ZabbixApiMethod.Parse(string)"/> for any other,
/// equal whatever case it is written in, and never text converted on the way (ADR 0013, section 7).
/// </summary>
public sealed class ZabbixApiMethodTests
{
    [Fact]
    public void A_method_equals_the_same_name_in_any_case_and_keeps_the_name_it_was_given()
    {
        var upper = new ZabbixApiMethod("HOST.GET");

        Assert.Equal(ZabbixApiMethod.HostGet, upper);
        Assert.True(upper == ZabbixApiMethod.HostGet);
        Assert.False(upper != ZabbixApiMethod.HostGet);
        Assert.True(ZabbixApiMethod.HostGet.Equals((object)upper));
        Assert.False(ZabbixApiMethod.HostGet.Equals("host.get"));
        Assert.Equal(ZabbixApiMethod.HostGet.GetHashCode(), upper.GetHashCode());
        Assert.NotEqual(ZabbixApiMethod.HostGet, ZabbixApiMethod.HostCreate);
        Assert.Equal("host.get", ZabbixApiMethod.HostGet.Name);
        Assert.Equal("host.get", ZabbixApiMethod.HostGet.ToString());
        Assert.Equal("HOST.GET", upper.Name);
    }

    [Fact]
    public void Parse_gives_a_known_method_the_description_s_spelling_and_keeps_an_unknown_one()
    {
        Assert.Equal("host.get", ZabbixApiMethod.Parse("Host.Get").Name);
        Assert.Equal("configuration.import", ZabbixApiMethod.Parse("CONFIGURATION.IMPORT").Name);
        Assert.Equal("acme.ping", ZabbixApiMethod.Parse("acme.ping").Name);
        Assert.False(ZabbixApiMethods.TryGet(ZabbixApiMethod.Parse("acme.ping"), out ZabbixApiMethodInfo? unknown));
        Assert.Null(unknown);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("host.get.all")]
    [InlineData(".get")]
    [InlineData("host.")]
    [InlineData("host get")]
    [InlineData("host.get ")]
    public void A_name_that_is_not_an_object_and_a_method_is_refused(string name)
    {
        Assert.Throws<FormatException>(() => new ZabbixApiMethod(name));
        Assert.Throws<FormatException>(() => ZabbixApiMethod.Parse(name));
    }

    [Fact]
    public void A_missing_name_is_refused_and_the_default_method_names_none()
    {
        Assert.Throws<ArgumentNullException>(() => new ZabbixApiMethod(null!));
        Assert.Throws<ArgumentException>(() => new ZabbixApiMethod(string.Empty));
        Assert.Throws<ArgumentException>(() => new ZabbixApiMethod("   "));
        Assert.Equal(string.Empty, default(ZabbixApiMethod).Name);
        Assert.Throws<ArgumentException>(() => ZabbixApiMethods.Supports(default, new Version(7, 0, 0)));
    }

    [Fact]
    public void No_text_becomes_a_method_without_the_caller_saying_so()
    {
        // As HttpMethod: new ZabbixApiMethod("x.y") or Parse, and no implicit or explicit conversion from a string.
        Assert.DoesNotContain(typeof(ZabbixApiMethod).GetMethods(BindingFlags.Public | BindingFlags.Static), method => method.Name is "op_Implicit" or "op_Explicit");
    }

    [Fact]
    public void Every_method_the_table_knows_is_its_own_entry_and_a_property_of_its_own()
    {
        PropertyInfo[] properties = [.. typeof(ZabbixApiMethod).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(property => property.PropertyType == typeof(ZabbixApiMethod))];
        HashSet<ZabbixApiMethod> named = [.. properties.Select(property => (ZabbixApiMethod)property.GetValue(null)!)];

        Assert.All(ZabbixApiMethods.All, entry => Assert.Equal(entry.Key, entry.Value.Method));
        Assert.All(ZabbixApiMethods.All.Keys, method => Assert.Contains(method, named));
        Assert.Equal(ZabbixApiMethods.All.Count, properties.Length);
    }

    [Fact]
    public async Task WithTimeout_sets_the_timeout_of_the_method_it_names()
    {
        var services = new ServiceCollection();
        IZabbixClientBuilder builder = services.AddZabbixClient()
            .Configure(options => options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"))
            .WithTimeout(ZabbixApiMethod.ConfigurationImport, TimeSpan.FromMinutes(5));
        await using ServiceProvider provider = services.BuildServiceProvider();

        ZabbixClientOptions options = provider.GetRequiredService<IOptionsMonitor<ZabbixClientOptions>>().Get(builder.Name);

        Assert.Equal(TimeSpan.FromMinutes(5), options.Timeouts["configuration.import"]);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Timeouts["CONFIGURATION.IMPORT"]);
    }

    [Fact]
    public void One_member_answers_whether_a_method_may_be_sent_again()
    {
        // The retry gate asks ZabbixApiMethods.IsIdempotent; no second answer can drift from it.
        MethodInfo answer = Assert.Single(
            Libraries.PublicTypes.SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)),
            method => method.Name == "IsIdempotent");

        Assert.Equal(typeof(ZabbixApiMethods), answer.DeclaringType);
        Assert.True(ZabbixApiMethods.IsIdempotent(ZabbixApiMethod.HostGet));
        Assert.False(ZabbixApiMethods.IsIdempotent(ZabbixApiMethod.HostCreate));
    }
}
