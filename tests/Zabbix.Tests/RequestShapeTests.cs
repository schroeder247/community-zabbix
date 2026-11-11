// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix.Tests;

/// <summary>
/// Writes take a lambda, as reads do (ADR 0013, section 4): a request's required fields are <c>required</c> and its
/// constructor takes them, its optional fields have setters, a create takes a lambda for the rest, a node updates its object
/// by lambda with the id filled in, and a nested value needs no <c>new</c>. Each rule is checked on the wire through the
/// scripted API, and over every generated record and client by reflection.
/// </summary>
public sealed class RequestShapeTests
{
    private static readonly Assembly ApiModel = typeof(ZabbixHostCreateRequest).Assembly;

    private sealed record Api(ZabbixClient Zabbix, ScriptedApi Script, ServiceProvider Provider) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }

    private static Api Build(Func<ScriptedApi, ScriptedApi> script)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => script(new ScriptedApi()));
        services.AddZabbixClient().Configure(options => { options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"); options.ApiToken = "tok"; });
        services.AddHttpClient(ZabbixHttp.GetClientName()).ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<ScriptedApi>());
        ServiceProvider provider = services.BuildValidated();
        return new Api(provider.GetRequiredService<ZabbixClient>(), provider.GetRequiredService<ScriptedApi>(), provider);
    }

    // ----- a create takes a lambda for the fields it doesn't require ---------------------------------------------------

    [Fact]
    public async Task A_create_s_lambda_sets_the_other_fields_beside_the_required_ones()
    {
        using Api api = Build(script => script.On("host.create", """{"hostids":["10084"]}"""));
        ulong linux = 42;

        ulong id = await api.Zabbix.Hosts.CreateAsync("web-02", [linux], h =>
        {
            h.Interfaces = [ZabbixInterface.Agent(IPAddress.Parse("10.0.0.2"))];
            h.Tags = [("env", "prod")];
        }, TestContext.Current.CancellationToken);

        Assert.Equal(10084UL, id);
        JsonElement sent = api.Script.ParamsOf("host.create").Single();
        Assert.Equal("web-02", sent.GetProperty("host").GetString());
        Assert.Equal("42", sent.GetProperty("groups")[0].GetProperty("groupid").ToString());
        Assert.Equal("""{"type":1,"ip":"10.0.0.2","dns":"","port":"10050","useip":1,"main":1}""", sent.GetProperty("interfaces")[0].GetRawText());
        Assert.Equal("""[{"tag":"env","value":"prod"}]""", sent.GetProperty("tags").GetRawText());
    }

    [Fact]
    public async Task A_create_s_expression_lambda_sends_only_what_it_sets()
    {
        using Api api = Build(script => script.On("host.create", """{"hostids":["7"]}"""));

        await api.Zabbix.Hosts.CreateAsync("web-02", [42UL], h => h.Status = ZabbixHostStatus.Disabled, TestContext.Current.CancellationToken);

        JsonElement sent = api.Script.ParamsOf("host.create").Single();
        Assert.Equal(["groups", "status", "host"], [.. sent.EnumerateObject().Select(field => field.Name)]);
        Assert.Equal(1, sent.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task A_target_typed_request_goes_through_the_request_s_own_overload()
    {
        using Api api = Build(script => script.On("host.create", """{"hostids":["7"]}"""));

        await api.Zabbix.Hosts.CreateAsync(new("web-03", [42UL]) { VisibleName = "Web 3", Tags = [("env", "prod")] }, TestContext.Current.CancellationToken);

        JsonElement sent = api.Script.ParamsOf("host.create").Single();
        Assert.Equal("web-03", sent.GetProperty("host").GetString());
        Assert.Equal("Web 3", sent.GetProperty("name").GetString());
        Assert.Equal("env", sent.GetProperty("tags")[0].GetProperty("tag").GetString());
    }

    [Fact]
    public async Task A_create_with_one_required_field_takes_a_lambda_too()
    {
        using Api api = Build(script => script.On("hostgroup.create", """{"groupids":["3"]}"""));

        ulong id = await api.Zabbix.HostGroups.CreateAsync("Linux servers", g => g.Uuid = "6f1ed29f0b1a4b4e8c3a8c4b2e5d7a90", TestContext.Current.CancellationToken);

        Assert.Equal(3UL, id);
        JsonElement sent = api.Script.ParamsOf("hostgroup.create").Single();
        Assert.Equal("Linux servers", sent.GetProperty("name").GetString());
        Assert.Equal("6f1ed29f0b1a4b4e8c3a8c4b2e5d7a90", sent.GetProperty("uuid").GetString());
    }

    [Fact]
    public async Task A_create_s_lambda_cannot_be_null_and_nothing_is_sent()
    {
        using Api api = Build(script => script.On("host.create", """{"hostids":["7"]}"""));

        await Assert.ThrowsAsync<ArgumentNullException>(() => api.Zabbix.Hosts.CreateAsync("web-02", [42UL], (Action<ZabbixHostCreateRequest>)null!, TestContext.Current.CancellationToken));

        Assert.Empty(api.Script.Calls);
    }

    [Fact]
    public async Task A_double_that_overrides_the_request_s_overload_serves_the_short_and_the_lambda_forms()
    {
        var hosts = new RecordingHosts();

        await hosts.CreateAsync("web-02", [42UL], TestContext.Current.CancellationToken);
        await hosts.CreateAsync("web-03", [42UL], h => h.VisibleName = "Web 3", TestContext.Current.CancellationToken);

        Assert.Equal(["web-02", "web-03"], [.. hosts.Created.Select(request => request.HostName)]);
        Assert.Equal("Web 3", hosts.Created[1].VisibleName);
    }

    // ----- a node updates its object by lambda, with the id filled in -------------------------------------------------

    [Fact]
    public async Task A_host_node_reads_its_id_and_updates_what_the_lambda_sets()
    {
        using Api api = Build(script => script
            .On("host.get", """[{"hostid":"10084","host":"web-02"}]""")
            .On("host.update", """{"hostids":["10084"]}"""));

        await api.Zabbix.Hosts["web-02"].UpdateAsync(h => h.Status = ZabbixHostStatus.Disabled, TestContext.Current.CancellationToken);

        Assert.Equal("web-02", api.Script.ParamsOf("host.get").Single().GetProperty("filter").GetProperty("host").GetString());
        Assert.Equal("""{"hostid":"10084","status":1}""", api.Script.ParamsOf("host.update").Single().GetRawText());
    }

    [Fact]
    public async Task A_group_node_renames_its_group()
    {
        using Api api = Build(script => script
            .On("hostgroup.get", """[{"groupid":"5","name":"Linux"}]""")
            .On("hostgroup.update", """{"groupids":["5"]}"""));

        await api.Zabbix.HostGroups["Linux"].UpdateAsync(g => g.Name = "Linux servers", TestContext.Current.CancellationToken);

        Assert.Equal("""{"groupid":"5","name":"Linux servers"}""", api.Script.ParamsOf("hostgroup.update").Single().GetRawText());
    }

    [Fact]
    public async Task A_node_by_id_updates_without_reading_first()
    {
        using Api api = Build(script => script.On("item.update", """{"itemids":["7"]}"""));

        ZabbixUpdatableObjectNode<ZabbixItem, ZabbixItemUpdateRequest> item = api.Zabbix.Items[7];
        await item.UpdateAsync(i => i.Delay = TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        Assert.Empty(api.Script.ParamsOf("item.get"));
        Assert.Equal("""{"itemid":"7","delay":"5m"}""", api.Script.ParamsOf("item.update").Single().GetRawText());
    }

    [Fact]
    public async Task An_item_node_reads_the_item_on_its_host_and_updates_it()
    {
        using Api api = Build(script => script
            .On("item.get", """[{"itemid":"23","key_":"agent.ping","hostid":"10084"}]""")
            .On("item.update", """{"itemids":["23"]}"""));

        await api.Zabbix.Hosts["web-01"].Items["agent.ping"].UpdateAsync(i => i.Status = ZabbixItemStatus.Disabled, TestContext.Current.CancellationToken);

        Assert.Equal("""{"itemid":"23","status":1}""", api.Script.ParamsOf("item.update").Single().GetRawText());
    }

    [Fact]
    public async Task A_node_of_no_such_object_updates_nothing()
    {
        using Api api = Build(script => script.On("host.get", "[]").On("hostgroup.get", "[]"));

        await Assert.ThrowsAsync<ZabbixNotFoundException>(() => api.Zabbix.Hosts["none"].UpdateAsync(h => h.Status = ZabbixHostStatus.Disabled, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ZabbixNotFoundException>(() => api.Zabbix.HostGroups["none"].UpdateAsync(g => g.Name = "x", TestContext.Current.CancellationToken));

        Assert.Empty(api.Script.ParamsOf("host.update"));
        Assert.Empty(api.Script.ParamsOf("hostgroup.update"));
    }

    [Fact]
    public void A_node_s_lambda_cannot_be_null_and_is_refused_before_any_call()
    {
        using Api api = Build(script => script);

        // thrown by the call itself, not by the task it would return (TAP: a usage error is synchronous)
        Assert.Throws<ArgumentNullException>(() => { _ = api.Zabbix.Hosts["web-02"].UpdateAsync(null!, TestContext.Current.CancellationToken); });
        Assert.Throws<ArgumentNullException>(() => { _ = api.Zabbix.HostGroups["Linux"].UpdateAsync(null!, TestContext.Current.CancellationToken); });
        Assert.Throws<ArgumentNullException>(() => { _ = api.Zabbix.Items[7].UpdateAsync(null!, TestContext.Current.CancellationToken); });
        Assert.Empty(api.Script.Calls);
    }

    // ----- nested values without new ------------------------------------------------------------------------------------

    public static TheoryData<string, string> Interfaces => new()
    {
        { "agent at an address", """{"type":1,"ip":"10.0.0.2","dns":"","port":"10050","useip":1}""" },
        { "agent at a name", """{"type":1,"ip":"","dns":"web-02.example","port":"10050","useip":0}""" },
        { "agent on a port", """{"type":1,"ip":"10.0.0.2","dns":"","port":"10051","useip":1}""" },
        { "snmp", """{"type":2,"ip":"10.0.0.2","dns":"","port":"161","useip":1,"details":{"version":2,"community":"{$SNMP_COMMUNITY}"}}""" },
        { "snmp with a community", """{"type":2,"ip":"","dns":"switch-01.example","port":"1161","useip":0,"details":{"version":2,"community":"public"}}""" },
        { "snmpv3", """{"type":2,"ip":"10.0.0.2","dns":"","port":"161","useip":1,"details":{"version":3,"securityname":"monitor"}}""" },
        { "snmpv1 without a community", """{"type":2,"ip":"10.0.0.2","dns":"","port":"161","useip":1,"details":{"version":1,"community":"{$SNMP_COMMUNITY}"}}""" },
        { "ipmi", """{"type":3,"ip":"10.0.0.2","dns":"","port":"623","useip":1}""" },
        { "jmx", """{"type":4,"ip":"","dns":"app-01.example","port":"12345","useip":0}""" },
        { "ipv6", """{"type":1,"ip":"2001:db8::2","dns":"","port":"10050","useip":1}""" },
    };

    [Theory]
    [MemberData(nameof(Interfaces))]
    public void An_interface_factory_writes_what_Zabbix_needs_for_its_kind(string kind, string json)
    {
        IPAddress address = IPAddress.Parse("10.0.0.2");
        ZabbixHostInterfaceRequest made = kind switch
        {
            "agent at an address" => ZabbixInterface.Agent(address),
            "agent at a name" => ZabbixInterface.Agent("web-02.example"),
            "agent on a port" => ZabbixInterface.Agent(address, 10051),
            "snmp" => ZabbixInterface.Snmp(address),
            "snmp with a community" => ZabbixInterface.Snmp("switch-01.example", 1161, "public"),
            "snmpv3" => ZabbixInterface.Snmp(address, 161, new ZabbixHostInterfaceDetailRequest { Version = ZabbixSnmpVersion.V3, SecurityName = "monitor" }),
            "snmpv1 without a community" => ZabbixInterface.Snmp(address, 161, new ZabbixHostInterfaceDetailRequest { Version = ZabbixSnmpVersion.V1 }),
            "ipmi" => ZabbixInterface.Ipmi(address),
            "jmx" => ZabbixInterface.Jmx("app-01.example"),
            "ipv6" => ZabbixInterface.Agent(IPAddress.Parse("2001:db8::2")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Assert.Equal(json, JsonSerializer.Serialize(made, ZabbixJson.Options));
    }

    [Fact]
    public void The_first_interface_of_each_type_is_the_main_one()
    {
        var request = new ZabbixHostCreateRequest("web-02", [42UL])
        {
            Interfaces = [ZabbixInterface.Agent(IPAddress.Parse("10.0.0.2")), ZabbixInterface.Agent("web-02.example"), ZabbixInterface.Snmp(IPAddress.Parse("10.0.0.2"))],
        };

        Assert.Equal([ZabbixInterfacePriority.Primary, ZabbixInterfacePriority.Secondary, ZabbixInterfacePriority.Primary], [.. request.Interfaces!.Select(i => i.Main)]);
    }

    [Fact]
    public void An_interface_marked_main_keeps_the_mark_and_the_others_of_its_type_are_not_main()
    {
        var request = new ZabbixHostUpdateRequest(10084)
        {
            Interfaces =
            [
                ZabbixInterface.Agent(IPAddress.Parse("10.0.0.2")),
                ZabbixInterface.Agent(IPAddress.Parse("10.0.0.3")) with { Main = ZabbixInterfacePriority.Primary },
                ZabbixInterface.Jmx(IPAddress.Parse("10.0.0.2")) with { Main = ZabbixInterfacePriority.Secondary },
                ZabbixInterface.Jmx(IPAddress.Parse("10.0.0.3")),
            ],
        };

        Assert.Equal([ZabbixInterfacePriority.Secondary, ZabbixInterfacePriority.Primary, ZabbixInterfacePriority.Secondary, ZabbixInterfacePriority.Primary], [.. request.Interfaces!.Select(i => i.Main)]);
    }

    [Fact]
    public void An_interface_factory_refuses_what_is_not_an_address_or_a_port_where_it_is_passed()
    {
        Assert.Equal("address", Assert.Throws<ArgumentNullException>(() => ZabbixInterface.Agent((IPAddress)null!)).ParamName);
        Assert.Equal("dnsName", Assert.Throws<ArgumentException>(() => ZabbixInterface.Agent(" ")).ParamName);
        Assert.Equal("port", Assert.Throws<ArgumentOutOfRangeException>(() => ZabbixInterface.Ipmi(IPAddress.Loopback, 65536)).ParamName);
        Assert.Equal("port", Assert.Throws<ArgumentOutOfRangeException>(() => ZabbixInterface.Jmx("app-01", -1)).ParamName);
        Assert.Equal("community", Assert.Throws<ArgumentException>(() => ZabbixInterface.Snmp(IPAddress.Loopback, 161, "")).ParamName);
        Assert.Equal("details", Assert.Throws<ArgumentNullException>(() => ZabbixInterface.Snmp(IPAddress.Loopback, 161, (ZabbixHostInterfaceDetailRequest)null!)).ParamName);
    }

    [Fact]
    public async Task An_interface_goes_on_a_host_that_exists_as_the_main_one_of_its_type()
    {
        using Api api = Build(script => script.On("hostinterface.create", """{"interfaceids":["31"]}"""));

        ulong id = await api.Zabbix.HostInterfaces.CreateAsync(10084, ZabbixInterface.Snmp(IPAddress.Parse("10.0.0.2")), TestContext.Current.CancellationToken);

        Assert.Equal(31UL, id);
        Assert.Equal(
            """{"hostid":"10084","type":2,"ip":"10.0.0.2","dns":"","port":"161","useip":1,"main":1,"details":{"version":2,"community":"{$SNMP_COMMUNITY}"}}""",
            api.Script.ParamsOf("hostinterface.create").Single().GetRawText());
    }

    [Fact]
    public async Task An_interface_without_a_type_is_refused_before_it_is_sent()
    {
        using Api api = Build(script => script.On("hostinterface.create", """{"interfaceids":["31"]}"""));

        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(() => api.Zabbix.HostInterfaces.CreateAsync(10084, new ZabbixHostInterfaceRequest { Port = 10050 }, TestContext.Current.CancellationToken));

        Assert.Equal("hostInterface", refused.ParamName);
        Assert.Empty(api.Script.Calls);
    }

    [Fact]
    public void A_tag_converts_from_a_pair_and_from_its_named_alternative()
    {
        ZabbixTagRequest tag = ("env", "prod");

        Assert.Equal(new ZabbixTagRequest("env") { Value = "prod" }, tag);
        Assert.Equal(tag, ZabbixTagRequest.FromValueTuple(("env", "prod")));
        Assert.Equal("""{"tag":"env","value":"prod"}""", JsonSerializer.Serialize(tag, ZabbixJson.Options));
    }

    [Fact]
    public void A_user_macro_converts_from_a_pair_on_a_create_and_on_an_update()
    {
        ZabbixHostMacroRequest created = ("{$SNMP_COMMUNITY}", "public");
        ZabbixHostUpdateMacroRequest updated = ("{$SNMP_COMMUNITY}", "private");

        Assert.Equal(new ZabbixHostMacroRequest("{$SNMP_COMMUNITY}", "public"), created);
        Assert.Equal("""{"macro":"{$SNMP_COMMUNITY}","value":"private"}""", JsonSerializer.Serialize(updated, ZabbixJson.Options));
    }

    /// <summary>The records a pair converts to: every tag a request sets, and every user macro, the update's included.</summary>
    [Fact]
    public void Every_tag_and_user_macro_record_converts_from_a_pair_and_nothing_else_does()
    {
        string[] converting = [.. ApiModel.GetExportedTypes()
            .Where(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(method => method.Name == "op_Implicit" && method.GetParameters() is [{ ParameterType: var from }] && from == typeof(ValueTuple<string, string>)))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)];

        Assert.Equal(
            [
                "ZabbixActionOperationTagRequest", "ZabbixDiscoveryRuleOverrideOperationTagRequest", "ZabbixDiscoveryRulePrototypeOverrideOperationTagRequest",
                "ZabbixHostMacroRequest", "ZabbixHostMassAddMacroRequest", "ZabbixHostMassMacroRequest", "ZabbixHostPrototypeMacroRequest",
                "ZabbixHostPrototypeUpdateMacroRequest", "ZabbixHostUpdateMacroRequest", "ZabbixRoleServiceTagRequest", "ZabbixTagRequest",
                "ZabbixTemplateMacroRequest", "ZabbixTemplateMassAddMacroRequest", "ZabbixTemplateMassMacroRequest", "ZabbixTemplateUpdateMacroRequest",
            ],
            converting);
    }

    // ----- every generated record and client follows the rules ------------------------------------------------------------

    /// <summary>
    /// A record with <c>required</c> members has a constructor that takes them, marked <c>SetsRequiredMembers</c>, in the
    /// order a short create takes them (a list last), and one that takes nothing, for an initializer the compiler checks.
    /// </summary>
    [Fact]
    public void Every_generated_record_with_required_members_has_a_constructor_that_sets_them()
    {
        List<string> offenders = [];
        int checkedRecords = 0;
        foreach (Type record in Records())
        {
            PropertyInfo[] required = [.. RequiredMembers(record)];
            if (required.Length == 0) continue;
            checkedRecords++;
            if (record.GetConstructor(Type.EmptyTypes) is null) offenders.Add($"{record.Name}: no constructor that takes nothing");
            ConstructorInfo[] setting = [.. record.GetConstructors().Where(constructor => constructor.IsDefined(typeof(SetsRequiredMembersAttribute)))];
            if (setting is not [ConstructorInfo constructor])
            {
                offenders.Add($"{record.Name}: {setting.Length} public constructors set the required members");
                continue;
            }
            string[] expected = [.. required.OrderBy(property => IsList(property.PropertyType)).Select(property => CamelCase(property.Name))];
            string[] taken = [.. constructor.GetParameters().Select(parameter => parameter.Name!)];
            if (!expected.SequenceEqual(taken, StringComparer.Ordinal))
            {
                offenders.Add($"{record.Name}({string.Join(", ", taken)}): expected ({string.Join(", ", expected)})");
                continue;
            }
            object?[] arguments = [.. constructor.GetParameters().Select(parameter => Sample(parameter.ParameterType))];
            object built = constructor.Invoke(arguments);
            for (int i = 0; i < arguments.Length; i++)
            {
                PropertyInfo member = required.Single(property => string.Equals(property.Name, taken[i], StringComparison.OrdinalIgnoreCase));
                if (!Equals(member.GetValue(built), arguments[i])) offenders.Add($"{record.Name}.{member.Name}: the constructor doesn't set it");
            }
        }

        Assert.True(checkedRecords > 300, $"only {checkedRecords} records have required members");
        DesignConformanceTests.AssertNone(offenders);
    }

    /// <summary>
    /// A request a client sends has setters on its fields, which a lambda sets, the required ones too, so a lambda can change
    /// what a short create filled in (ADR 0013, section 3: an item created from a typed key is named after its key until the
    /// lambda names it); an id it requires is set once (<c>init</c>), so a node's lambda can't change the id it updates.
    /// </summary>
    [Fact]
    public void Every_request_s_fields_have_setters_and_its_required_ids_are_set_once()
    {
        List<string> offenders = [];
        Type[] requests = [.. SentRequests()];
        foreach (Type request in requests)
        {
            foreach (PropertyInfo property in request.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.Name != "AdditionalFields"))
            {
                bool requiredId = property.IsDefined(typeof(RequiredMemberAttribute)) && property.PropertyType == typeof(ulong);
                if (property.SetMethod is not { IsPublic: true } setter) offenders.Add($"{request.Name}.{property.Name}: no setter");
                else if (InitOnly(setter) != requiredId) offenders.Add($"{request.Name}.{property.Name}: {(requiredId ? "a required id, and settable after construction" : "set only in an initializer")}");
            }
        }

        Assert.True(requests.Length > 100, $"only {requests.Length} requests");
        DesignConformanceTests.AssertNone(offenders);
    }

    /// <summary>An update names the object it changes, so its id is required: <c>graph.update</c>'s too, which the extraction can't see.</summary>
    [Fact]
    public void Every_update_requires_the_id_of_what_it_changes()
    {
        List<string> offenders = [];
        foreach (Type client in Clients())
        {
            if (client.GetMethods().FirstOrDefault(method => method.Name == "UpdateAsync" && method.GetParameters() is [{ Name: "request" }, { ParameterType.Name: nameof(CancellationToken) }]) is not { } update) continue;
            Type request = update.GetParameters()[0].ParameterType;
            if (client.GetProperty("Item", [typeof(ulong)]) is null) continue;   // an update of a singleton (settings, housekeeping) names nothing
            if (RequiredMembers(request).ToArray() is not [{ PropertyType: var id }] || id != typeof(ulong)) offenders.Add($"{request.Name}: requires {string.Join(", ", RequiredMembers(request).Select(property => property.Name))}");
        }

        Assert.True(typeof(ZabbixGraphUpdateRequest).GetProperty(nameof(ZabbixGraphUpdateRequest.GraphId))!.IsDefined(typeof(RequiredMemberAttribute)));
        Assert.True(typeof(ZabbixGraphPrototypeUpdateRequest).GetProperty(nameof(ZabbixGraphPrototypeUpdateRequest.GraphId))!.IsDefined(typeof(RequiredMemberAttribute)));
        DesignConformanceTests.AssertNone(offenders);
    }

    /// <summary>
    /// A create from the fields it requires also takes a lambda for the rest, virtual, with its token optional last as every
    /// overload's is (ADR 0013, section 6).
    /// </summary>
    [Fact]
    public void Every_short_create_has_a_lambda_form()
    {
        List<string> offenders = [];
        int shortCreates = 0;
        foreach (Type client in Clients())
        {
            MethodInfo[] creates = [.. client.GetMethods().Where(method => method.Name == "CreateAsync")];
            if (creates.FirstOrDefault(method => method.GetParameters() is [{ Name: "request" }, _]) is not { } own) continue;
            Type action = typeof(Action<>).MakeGenericType(own.GetParameters()[0].ParameterType);
            foreach (MethodInfo create in creates.Where(method => method.GetParameters() is [.. var fields, { ParameterType: var last }] && last == typeof(CancellationToken)
                && fields.Length > 0 && fields[0].Name is not ("request" or "requests") && fields.All(field => field.ParameterType != action) && client.Name != nameof(ZabbixHostInterfaceClient)))
            {
                shortCreates++;
                Type[] fields = [.. create.GetParameters()[..^1].Select(parameter => parameter.ParameterType)];
                if (LambdaForm(creates, create, action) is not { IsVirtual: true } configured)
                    offenders.Add($"{client.Name}.CreateAsync({string.Join(", ", fields.Select(field => field.Name))}): no virtual lambda form");
                else if (!configured.GetParameters()[^1].HasDefaultValue)
                    offenders.Add($"{client.Name}.CreateAsync({string.Join(", ", fields.Select(field => field.Name))}): the lambda form's token isn't optional");
            }
        }

        Assert.True(shortCreates > 40, $"only {shortCreates} short creates");
        DesignConformanceTests.AssertNone(offenders);
    }

    /// <summary>
    /// A node by id of a client that updates its objects updates its object, with the client's own update request: an
    /// updatable node, or a host's or a host group's own node, which their names reach too (ADR 0013, section 4).
    /// </summary>
    [Fact]
    public void Every_node_by_id_of_a_client_that_updates_updates()
    {
        List<string> offenders = [];
        int updatable = 0;
        foreach (Type client in Clients())
        {
            if (client.GetProperty("Item", [typeof(ulong)]) is not { } indexer) continue;
            if (client.GetMethods().FirstOrDefault(method => method.Name == "UpdateAsync" && method.GetParameters() is [{ Name: "requests" }, _]) is not { } update) continue;
            updatable++;
            Type request = update.GetParameters()[0].ParameterType.GetGenericArguments()[0];
            Type node = indexer.PropertyType;
            if (node.GetMethod("UpdateAsync", [typeof(Action<>).MakeGenericType(request), typeof(CancellationToken)]) is null)
                offenders.Add($"{client.Name}[ulong] is a {node.Name}, which doesn't update with {request.Name}");
            if (client.GetProperty("Item", [typeof(string)]) is { } byName && byName.PropertyType != node)
                offenders.Add($"{client.Name}[ulong] is a {node.Name}, and {client.Name}[string] a {byName.PropertyType.Name}");
        }

        Assert.True(updatable > 40, $"only {updatable} clients update by id");
        DesignConformanceTests.AssertNone(offenders);
    }

    /// <summary>
    /// A target-typed request converts to every parameter type there is, so the one request's overloads go first, with the
    /// compiler's own tie-break, wherever several requests are an overload beside it.
    /// </summary>
    [Fact]
    public void Every_one_request_overload_beside_a_several_goes_first()
    {
        List<string> offenders = [];
        foreach (Type client in Clients())
        {
            foreach (MethodInfo one in client.GetMethods().Where(method => method.GetParameters() is [{ Name: "request" }, ..]))
            {
                bool several = client.GetMethods().Any(method => method.Name == one.Name && method.GetParameters() is [{ Name: "requests" }, ..]);
                bool first = one.GetCustomAttribute<OverloadResolutionPriorityAttribute>() is { Priority: > 0 };
                if (several != first) offenders.Add($"{client.Name}.{one.Name}({string.Join(", ", one.GetParameters().Select(parameter => parameter.ParameterType.Name))}): {(several ? "beside several, and not first" : "first, with no several beside it")}");
            }
        }

        DesignConformanceTests.AssertNone(offenders);
    }

    // ----- the reflection beneath the rules -----------------------------------------------------------------------------

    private static IEnumerable<Type> Records() => ApiModel.GetExportedTypes().Where(type => type is { IsClass: true, IsAbstract: false } && type.GetMethod("<Clone>$") is not null);

    private static IEnumerable<PropertyInfo> RequiredMembers(Type record)
        => record.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.IsDefined(typeof(RequiredMemberAttribute)));

    private static IEnumerable<Type> Clients()
        => typeof(ZabbixHostClient).Assembly.GetExportedTypes().Where(type => type.Name.EndsWith("Client", StringComparison.Ordinal) && type.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes) is not null);

    // The records a client sends as a method's request: the one parameter of a call, beside its token.
    private static IEnumerable<Type> SentRequests()
        => Clients()
            .SelectMany(client => client.GetMethods())
            .Select(method => method.GetParameters())
            .Where(parameters => parameters is [{ Name: "request" }] or [{ Name: "request" }, { ParameterType.Name: nameof(CancellationToken) }])
            .Select(parameters => parameters[0].ParameterType)
            .Where(type => type.Assembly == ApiModel)
            .Distinct();

    private static bool InitOnly(MethodInfo setter) => setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));

    private static bool IsList(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>);

    private static object? Sample(Type type)
    {
        Type bare = Nullable.GetUnderlyingType(type) ?? type;
        if (bare == typeof(string)) return "x";
        if (bare == typeof(ulong)) return 7UL;
        if (bare == typeof(bool)) return true;
        if (bare == typeof(DateTimeOffset)) return DateTimeOffset.FromUnixTimeSeconds(1);
        if (bare.IsEnum) return Enum.GetValues(bare).GetValue(0);
        if (bare.IsPrimitive) return Convert.ChangeType(1, bare, System.Globalization.CultureInfo.InvariantCulture);
        if (IsList(bare)) return Array.CreateInstance(bare.GetGenericArguments()[0], 0);
        if (bare.IsGenericType && bare.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)) return Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(bare.GetGenericArguments()));
        return Activator.CreateInstance(bare);
    }

    private sealed class RecordingHosts : ZabbixHostClient
    {
        public List<ZabbixHostCreateRequest> Created { get; } = [];

        public override Task<ulong> CreateAsync(ZabbixHostCreateRequest request, CancellationToken cancellationToken)
        {
            Created.Add(request);
            return Task.FromResult(1UL);
        }
    }

    // A parameter's name for a member, as the capitalization conventions write one: a leading acronym is lower case whole
    // (IP: ip), and before a word all but the word's capital (IPRange: ipRange).
    private static string CamelCase(string member)
    {
        int capitals = 0;
        while (capitals < member.Length && char.IsUpper(member[capitals])) capitals++;
        int lower = capitals <= 1 ? 1 : capitals == member.Length ? capitals : capitals - 1;
        return new string([.. member[..lower].Select(char.ToLowerInvariant)]) + member[lower..];
    }

    // The overload of create that takes create's parameters, then the lambda, then the token: a generic create's by its
    // generic parameters (CreateAsync<T>(hostId, key, type) beside CreateAsync<T>(hostId, key, type, configure)).
    private static MethodInfo? LambdaForm(MethodInfo[] creates, MethodInfo create, Type action)
    {
        Type[] fields = [.. create.GetParameters()[..^1].Select(parameter => parameter.ParameterType)];
        return creates.FirstOrDefault(other => other.GetParameters() is { } parameters && parameters.Length == fields.Length + 2
            && other.GetGenericArguments().Length == create.GetGenericArguments().Length
            && parameters[^2].ParameterType == action && parameters[^1].ParameterType == typeof(CancellationToken)
            && fields.Select((field, i) => Same(field, parameters[i].ParameterType)).All(same => same));
    }

    // Two parameter types are the same, a generic method's type parameters by their position.
    private static bool Same(Type one, Type other)
        => one.IsGenericParameter || other.IsGenericParameter
            ? one.IsGenericParameter && other.IsGenericParameter && one.GenericParameterPosition == other.GenericParameterPosition
            : one.IsGenericType && other.IsGenericType
                ? one.GetGenericTypeDefinition() == other.GetGenericTypeDefinition() && one.GetGenericArguments().Zip(other.GetGenericArguments()).All(pair => Same(pair.First, pair.Second))
                : one == other;
}
