// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Community.Zabbix.Tests.Conformance;

namespace Community.Zabbix.Tests;

/// <summary>
/// Every API record the web frontend has a page for implements <see cref="IZabbixFrontendPage"/>, so
/// <see cref="ZabbixFrontend.UriFor"/> takes it as it is: the read model of the API object each
/// <see cref="ZabbixPageKind"/> is the page of, giving a target of that kind, and no other record. A kind added without
/// its record, a record renamed in the description, or a record that implements the interface with the wrong kind fails
/// here.
/// </summary>
public sealed partial class DesignConformanceTests
{
    /// <summary>The API object each kind of page belongs to: the <c>.get</c> the kind's documentation names.</summary>
    private static readonly IReadOnlyDictionary<ZabbixPageKind, string> PageObjects = new Dictionary<ZabbixPageKind, string>
    {
        [ZabbixPageKind.Host] = "host",
        [ZabbixPageKind.HostGroup] = "hostgroup",
        [ZabbixPageKind.Template] = "template",
        [ZabbixPageKind.TemplateGroup] = "templategroup",
        [ZabbixPageKind.Item] = "item",
        [ZabbixPageKind.Trigger] = "trigger",
        [ZabbixPageKind.Graph] = "graph",
        [ZabbixPageKind.DiscoveryRule] = "discoveryrule",
        [ZabbixPageKind.WebScenario] = "httptest",
        [ZabbixPageKind.Dashboard] = "dashboard",
        [ZabbixPageKind.Map] = "map",
        [ZabbixPageKind.Problem] = "problem",
        [ZabbixPageKind.Event] = "event",
        [ZabbixPageKind.Maintenance] = "maintenance",
        [ZabbixPageKind.User] = "user",
        [ZabbixPageKind.UserGroup] = "usergroup",
        [ZabbixPageKind.Proxy] = "proxy",
        [ZabbixPageKind.NetworkDiscoveryRule] = "drule",
        [ZabbixPageKind.Action] = "action",
        [ZabbixPageKind.MediaType] = "mediatype",
        [ZabbixPageKind.Script] = "script",
        [ZabbixPageKind.Service] = "service",
        [ZabbixPageKind.Sla] = "sla",
    };

    [Fact]
    public void Every_kind_of_page_names_the_api_object_it_belongs_to()
        => Assert.Equal(Enum.GetValues<ZabbixPageKind>().Order(), PageObjects.Keys.Order());

    [Fact]
    public void Every_record_with_a_frontend_page_implements_the_page_interface_with_its_kind()
    {
        IReadOnlyDictionary<string, string> models = ReadModels();
        List<string> offenders = [];
        foreach ((ZabbixPageKind kind, string apiObject) in PageObjects)
        {
            if (!models.TryGetValue(apiObject, out string? model))
            {
                offenders.Add($"{kind}: the description has no read model for {apiObject}");
                continue;
            }
            Type? record = typeof(ZabbixHost).Assembly.GetType("Community.Zabbix." + model);
            if (record is null) offenders.Add($"{kind}: no record {model} for {apiObject}");
            else if (!typeof(IZabbixFrontendPage).IsAssignableFrom(record)) offenders.Add($"{kind}: {model} does not implement {nameof(IZabbixFrontendPage)}");
            else if (((IZabbixFrontendPage)Activator.CreateInstance(record)!).ToPageTarget().Kind != kind) offenders.Add($"{kind}: {model} gives a target of another kind");
        }

        AssertNone(offenders);
    }

    [Fact]
    public void Only_the_records_with_a_frontend_page_implement_the_page_interface()
    {
        IReadOnlyDictionary<string, string> models = ReadModels();
        HashSet<string> pages = [.. PageObjects.Values.Select(apiObject => "Community.Zabbix." + models[apiObject])];

        List<string> strays = [.. Libraries.PublicTypes
            .Where(type => typeof(IZabbixFrontendPage).IsAssignableFrom(type) && !type.IsInterface && type != typeof(ZabbixPageTarget))
            .Select(type => type.FullName!)
            .Where(name => !pages.Contains(name))];

        AssertNone(strays);
    }

    // The read model of each API object, as the description names it (services[].model), with the generator's prefix.
    private static Dictionary<string, string> ReadModels()
    {
        using JsonDocument description = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema", "zabbix-api.json")));
        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement service in description.RootElement.GetProperty("services").EnumerateArray())
        {
            if (service.TryGetProperty("model", out JsonElement model) && model.GetString() is { } name)
                models[service.GetProperty("object").GetString()!] = name.StartsWith("Zabbix", StringComparison.Ordinal) ? name : "Zabbix" + name;
        }
        return models;
    }
}
