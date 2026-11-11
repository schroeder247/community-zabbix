// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// <c>Get-Zbx&lt;Entity&gt; -Id</c> and <c>New-</c>/<c>Set-</c>'s post-write readback do not always
/// select by <c>filter.&lt;idfield&gt;</c>, which some objects' <c>.get</c> rejects outright (e.g.
/// <c>template.get {"filter":{"templateid":[...]}}</c> answers <c>[]</c> even for a real id, though
/// <c>template.get {"templateids":[...]}</c> works). <see cref="ZabbixEntityShape{TEntity}.BuildIdSelection"/>
/// (generator-supplied per entity in <c>ZabbixCmdletEmitter.EmitEntities</c>) selects by the entity's own top-level
/// <c>*ids</c> option when its <c>*GetParameters</c> carries one, falling back to <c>filter.&lt;idfield&gt;</c> only
/// when it does not.
/// </summary>
public sealed class IdSelectionTests
{
    private static readonly Type[] GeneratedGetCommands = [.. typeof(ZabbixCmdlet).Assembly.GetExportedTypes()
        .Where(type => !type.IsAbstract && type.Namespace == "Community.Zabbix.PowerShell.Commands.Generated"
            && type.BaseType is { IsGenericType: true } baseType && baseType.GetGenericTypeDefinition() == typeof(ZabbixGetCmdlet<,>))];

    [Fact]
    public void Every_generated_entitys_id_selection_uses_its_own_ids_option_when_its_GetParameters_carries_one()
    {
        // Sweep: every generated
        // entity's ZabbixEntityShape.BuildIdSelection either sets the *GetParameters member whose wire name is
        // idField + "s" directly (never touching Filter), or, only when no such member exists, falls back to the
        // filter.<idfield> shape. Reflection over the compiled ZabbixEntities table, not the generator's own
        // source text, so this catches a regression even if the generator's code changes shape later.
        MethodInfo ofMethod = typeof(ZabbixEntities).GetMethod(nameof(ZabbixEntities.Of))!;
        Assert.NotEmpty(GeneratedGetCommands);

        foreach (Type command in GeneratedGetCommands)
        {
            Type entityType = command.BaseType!.GetGenericArguments()[0];
            Type parametersType = command.BaseType!.GetGenericArguments()[1];
            object shape = ofMethod.MakeGenericMethod(entityType).Invoke(null, null)!;
            string idField = (string)shape.GetType().GetProperty("IdField")!.GetValue(shape)!;

            // The own ids option takes ZabbixIds; a CUID-keyed entity's (auditlog's auditids) takes strings, so a numeric
            // selection has no option to go to and falls back to the filter, as the generator's list:id rule says.
            PropertyInfo? idsMember = parametersType.GetProperties()
                .FirstOrDefault(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name == idField + "s" && p.PropertyType == typeof(IReadOnlyList<ulong>));

            object built = shape.GetType().GetMethod(nameof(ZabbixEntityShape<object>.BuildIdSelection))!
                .Invoke(shape, [new ulong[] { 123 }])!;

            PropertyInfo filterProperty = parametersType.GetProperty("Filter")!;
            object? filter = filterProperty.GetValue(built);

            if (idsMember is not null)
            {
                Assert.Empty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(filter));
                var ids = Assert.IsAssignableFrom<IReadOnlyList<ulong>>(idsMember.GetValue(built));
                Assert.Equal<ulong>([123], ids);
            }
            else
            {
                Assert.NotNull(filter);
                var dictionary = Assert.IsAssignableFrom<IReadOnlyDictionary<string, ZabbixFieldValue>>(filter);
                Assert.True(dictionary.ContainsKey(idField), $"{command.Name}: no *ids option on {parametersType.Name}, expected the filter.{idField} fallback");
            }
        }
    }

    [Fact]
    public void The_fallback_shape_sends_filter_by_idfield_when_the_entity_has_no_ids_option()
    {
        // Direct unit test of ZabbixEntityShape.Create's fallback lambda shape (ZabbixCmdletEmitter emits this exact
        // shape for an entity whose *GetParameters carries no *ids option of its own — none currently do, so this
        // pins the fallback's own correctness independent of whether any live entity ever exercises it).
        var shape = ZabbixEntityShape.Create<ZabbixHostGroup, ZabbixHostGroupGetParameters>(
            "hostgroup", "groupid", "name",
            entity => entity.GroupId, entity => entity.Name,
            ZabbixApiJson.Default.ZabbixHostGroupGetParameters, ZabbixApiJson.Default.IReadOnlyListZabbixHostGroup,
            ids => new ZabbixHostGroupGetParameters
            {
                Filter = ids.Count == 0 ? null : new() { ["groupid"] = ids.ToArray() },
            },
            selection => new ZabbixHostGroupGetParameters { Output = selection.Output is { } output ? [.. output.Select(static name => new ZabbixField<ZabbixHostGroup>(name))] : null, Filter = new(selection.Filter), Search = new(selection.Search), SearchByAny = selection.SearchByAny, StartSearch = selection.StartSearch, SearchWildcardsEnabled = selection.SearchWildcardsEnabled, Limit = selection.Limit });

        IZabbixGetParameters built = shape.BuildIdSelection([5, 6]);
        var parameters = Assert.IsType<ZabbixHostGroupGetParameters>(built);

        Assert.Null(parameters.GroupIds);
        Assert.NotNull(parameters.Filter);
        ZabbixFieldValue value = Assert.Contains("groupid", parameters.Filter);
        Assert.Equal((ZabbixFieldValue)new[] { "5", "6" }, value);
    }

    [Fact]
    public async Task Get_ZbxTemplate_Id_selects_by_the_top_level_templateids_option_not_filter()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("template.get", """[{"templateid":"10822","host":"tpl"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxTemplate -Id 10822");

        Assert.Empty(run.Errors);
        JsonElement call = Assert.Single(server.ParamsOf("template.get"));
        Assert.Equal("""["10822"]""", call.GetProperty("templateids").GetRawText());
        Assert.False(call.TryGetProperty("filter", out _));
    }

    [Fact]
    public async Task Get_ZbxHostGroup_Id_selects_by_the_top_level_groupids_option()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"5","name":"Linux servers"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup -Id 5");

        Assert.Empty(run.Errors);
        JsonElement call = Assert.Single(server.ParamsOf("hostgroup.get"));
        Assert.Equal("""["5"]""", call.GetProperty("groupids").GetRawText());
        Assert.False(call.TryGetProperty("filter", out _));
    }

    [Fact]
    public async Task Get_ZbxItem_Id_selects_by_the_top_level_itemids_option()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", """[{"itemid":"100","name":"CPU load","key_":"system.cpu.load"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxItem -Id 100");

        Assert.Empty(run.Errors);
        JsonElement call = Assert.Single(server.ParamsOf("item.get"));
        Assert.Equal("""["100"]""", call.GetProperty("itemids").GetRawText());
        Assert.False(call.TryGetProperty("filter", out _));
    }

    [Fact]
    public async Task New_ZbxTemplate_reads_the_created_template_back_by_templateids_not_filter()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("template.create", """{"templateids":["10822"]}""");
        server.On("template.get", """[{"templateid":"10822","host":"tpl"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxTemplate -Name tpl -Group 1 -Confirm:$false");

        Assert.Empty(run.Errors);
        ZabbixTemplate created = Assert.IsType<ZabbixTemplate>(Assert.Single(run.Output).BaseObject);
        Assert.Equal(10822UL, created.TemplateId);
        JsonElement readback = Assert.Single(server.ParamsOf("template.get"));
        Assert.Equal("""["10822"]""", readback.GetProperty("templateids").GetRawText());
        Assert.False(readback.TryGetProperty("filter", out _));
    }
}
