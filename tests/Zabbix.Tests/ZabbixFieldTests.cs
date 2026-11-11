// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Text.Json;

namespace Community.Zabbix.Tests;

/// <summary>
/// A get's fields as values of their record: <see cref="ZabbixField{TRecord}"/> names one, an output or a <c>select</c>
/// option lists them or says <c>extend</c> or <c>count</c>, and the sort is one list of fields with their directions, sent
/// as <c>sortfield</c> and <c>sortorder</c>. A field the SDK doesn't know is constructed by its name.
/// </summary>
public sealed class ZabbixFieldTests
{
    [Fact]
    public void A_field_is_its_api_name_and_one_the_sdk_does_not_know_is_constructed_by_it()
    {
        var newer = new ZabbixField<ZabbixHost>("a_new_field");

        Assert.Equal("a_new_field", newer.Name);
        Assert.Equal("a_new_field", newer.ToString());
        Assert.Equal("name", ZabbixHostFields.VisibleName.Name);
        Assert.Equal(new ZabbixField<ZabbixHost>("hostid"), ZabbixHostFields.HostId);
        Assert.Equal(string.Empty, default(ZabbixField<ZabbixHost>).Name);
        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => new ZabbixField<ZabbixHost>(null!)).ParamName);
        Assert.Equal("name", Assert.Throws<ArgumentException>(() => new ZabbixField<ZabbixHost>(string.Empty)).ParamName);
    }

    [Fact]
    public void Output_and_select_options_send_their_record_s_fields_or_a_keyword()
    {
        var parameters = new ZabbixHostGetParameters
        {
            Output = [ZabbixHostFields.HostId, new ZabbixField<ZabbixHost>("a_new_field")],
            SelectInterfaces = [ZabbixHostInterfaceFields.InterfaceId, ZabbixHostInterfaceFields.IP, ZabbixHostInterfaceFields.Port],
            SelectTags = ZabbixOutput.Extend,
            SelectItems = [ZabbixItemFields.ItemId],
        };

        using JsonDocument sent = JsonDocument.Parse(JsonSerializer.Serialize(parameters, ZabbixApiJson.Default.ZabbixHostGetParameters));

        Assert.Equal("""["hostid","a_new_field"]""", sent.RootElement.GetProperty("output").GetRawText());
        Assert.Equal("""["interfaceid","ip","port"]""", sent.RootElement.GetProperty("selectInterfaces").GetRawText());
        Assert.Equal("extend", sent.RootElement.GetProperty("selectTags").GetString());
        Assert.Equal("""["itemid"]""", sent.RootElement.GetProperty("selectItems").GetRawText());
        Assert.Equal("hostid,a_new_field", parameters.Output!.ToString());
        Assert.Equal("extend", parameters.SelectTags!.ToString());
    }

    [Fact]
    public void An_output_is_read_as_a_keyword_one_name_or_a_list()
    {
        Assert.Equal("extend", Read("""{"output":"extend"}""").Output!.Keyword);
        Assert.Equal(ZabbixOutput.Of(ZabbixHostFields.HostId), Read("""{"output":"hostid"}""").Output);
        Assert.Equal(ZabbixOutput.Of(ZabbixHostFields.HostId, ZabbixHostFields.VisibleName), Read("""{"output":["hostid","name"]}""").Output);
        Assert.Throws<JsonException>(() => Read("""{"output":[""]}"""));
        Assert.Throws<JsonException>(() => Read("""{"output":7}"""));
    }

    [Fact]
    public void A_field_that_names_no_field_is_refused_in_an_output_and_in_a_sort()
    {
        Assert.Throws<ArgumentException>(() => ZabbixOutput.Of(default(ZabbixField<ZabbixHost>)));
        Assert.Throws<ArgumentException>(() => JsonSerializer.Serialize(new ZabbixHostGetParameters { Sort = [default(ZabbixField<ZabbixHost>)] }, ZabbixApiJson.Default.ZabbixHostGetParameters));
    }

    [Fact]
    public void A_sort_sends_each_field_with_its_direction()
    {
        Assert.Equal(
            """{"sortfield":["name","hostid"],"sortorder":["DESC","ASC"]}""",
            Serialize(new ZabbixHostGetParameters { Sort = [ZabbixHostFields.VisibleName.Descending(), ZabbixHostFields.HostId] }));
        Assert.Equal("""{"sortfield":["hostid"],"sortorder":["ASC"]}""", Serialize(new ZabbixHostGetParameters { Sort = [ZabbixHostFields.HostId.Ascending()] }));
        Assert.Equal("{}", Serialize(new ZabbixHostGetParameters { Sort = null }));
    }

    [Fact]
    public void A_sort_reads_one_name_and_a_shorter_sortorder_leaves_the_rest_ascending()
    {
        ZabbixHostGetParameters one = Read("""{"sortfield":"name"}""");
        ZabbixHostGetParameters shorter = Read("""{"sortfield":["name","hostid"],"sortorder":["DESC"]}""");

        Assert.Equal<ZabbixSort<ZabbixHost>>([ZabbixHostFields.VisibleName], one.Sort!);
        Assert.Equal<ZabbixSort<ZabbixHost>>([ZabbixHostFields.VisibleName.Descending(), ZabbixHostFields.HostId], shorter.Sort!);
        Assert.Equal(new ZabbixHostGetParameters { Sort = [ZabbixHostFields.VisibleName.Descending(), ZabbixHostFields.HostId] }, shorter);
        Assert.Equal(new ZabbixHostGetParameters { Sort = [ZabbixHostFields.VisibleName.Descending(), ZabbixHostFields.HostId] }.GetHashCode(), shorter.GetHashCode());
    }

    [Fact]
    public void A_field_converts_to_its_ascending_sort()
    {
        ZabbixSort<ZabbixHost> sort = ZabbixHostFields.HostId;

        Assert.Equal(ListSortDirection.Ascending, sort.Direction);
        Assert.Equal(sort, ZabbixSort<ZabbixHost>.FromZabbixField(ZabbixHostFields.HostId));
        Assert.Equal("hostid", sort.ToString());
        Assert.Equal("name DESC", ZabbixHostFields.VisibleName.Descending().ToString());
    }

    [Fact]
    public void Code_over_any_record_sets_its_output_and_sort_through_the_typed_interface()
    {
        var hosts = new ZabbixHostGetParameters();
        var events = new ZabbixEventGetParameters();

        NewestFirst(hosts, ZabbixHostFields.HostId);
        NewestFirst(events, ZabbixEventFields.Clock);

        Assert.Equal(ZabbixOutput.Of(ZabbixHostFields.HostId), hosts.Output);
        Assert.Equal<ZabbixSort<ZabbixHost>>([ZabbixHostFields.HostId.Descending()], hosts.Sort!);
        Assert.Equal<ZabbixSort<ZabbixEvent>>([ZabbixEventFields.Clock.Descending()], events.Sort!);
    }

    private static void NewestFirst<TRecord>(IZabbixGetParameters<TRecord> parameters, ZabbixField<TRecord> field)
    {
        parameters.Output = [field];
        parameters.Sort = [field.Descending()];
    }

    private static string Serialize(ZabbixHostGetParameters parameters) => JsonSerializer.Serialize(parameters, ZabbixApiJson.Default.ZabbixHostGetParameters);

    private static ZabbixHostGetParameters Read(string json) => JsonSerializer.Deserialize(json, ZabbixApiJson.Default.ZabbixHostGetParameters)!;
}
