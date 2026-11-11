// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// Pipelines — mixed object kinds in one stream, extra/missing properties, case variants,
/// <c>$null</c> elements, a producer that fails mid-stream, <c>Select-Object -First</c> downstream, and per-record
/// by-property-name binding into a batching <c>Set-</c>.
/// </summary>
public sealed class AdversarialPipelineTests
{
    [Fact]
    public async Task A_mixed_stream_removes_every_reference_and_writes_one_error_per_element_that_is_none()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10087", "web-01"))).On("host.delete", """{"hostids":[]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        var entity = new ZabbixHost { HostId = 10086, TechnicalName = "db-01" };

        Run run = shell.Invoke("@($entity, @{ hostid = '3' }, '4', 5, 'web-01', $null, [ulong]6) | Remove-ZbxHost -Confirm:$false",
            new Dictionary<string, object?> { ["entity"] = entity });

        Assert.True(server.ParamsOf("host.delete").Count() == 1, string.Join(" | ", run.Errors.Select(e => e.FullyQualifiedErrorId + ": " + e)));
        Assert.Equal("""["10086","4","5","10087","6"]""", Assert.Single(server.ParamsOf("host.delete")).GetRawText());
        Assert.Equal(2, run.Errors.Count);
        Assert.All(run.Errors, error => Assert.IsAssignableFrom<ParameterBindingException>(error.Exception));
    }

    [Fact]
    public async Task Null_elements_in_a_stream_are_one_error_each_and_the_rest_are_removed()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.delete", """{"hostids":[]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("@('1', $null, '2', $null) | Remove-ZbxHost -Confirm:$false");

        Assert.Equal("""["1","2"]""", Assert.Single(server.ParamsOf("host.delete")).GetRawText());
        Assert.Equal(2, run.Errors.Count);
    }

    [Theory]
    [InlineData("[pscustomobject]@{ HostId = '7' } | Get-ZbxHost")]
    [InlineData("[pscustomobject]@{ hostid = '7' } | Get-ZbxHost")]
    [InlineData("[pscustomobject]@{ HOSTID = '7'; Extra = 1; Name = 'ignored' } | Get-ZbxHost")]
    [InlineData("[pscustomobject]@{ Id = '7' } | Get-ZbxHost")]
    [InlineData("'7' | Get-ZbxHost")]
    [InlineData("7 | Get-ZbxHost")]
    public async Task Get_ZbxHost_binds_a_piped_id_by_value_or_by_property_name_in_any_case(string script)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("7", "web-07")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(script);

        Assert.Empty(run.Errors);
        Assert.Single(run.Output);
        Assert.Equal("""["7"]""", Assert.Single(server.ParamsOf("host.get")).GetProperty("hostids").GetRawText());
    }

    [Fact]
    public async Task An_object_with_no_bindable_property_is_one_binding_error_and_no_call()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("[pscustomobject]@{ Colour = 'red' } | Get-ZbxHost");

        // The binder's own refusal, id included: a Get-'s hidden InputObject takes the object and refuses it as the binder
        // refuses it on every other command, so a script branches on one id.
        ErrorRecord error = Assert.Single(run.Errors);
        Assert.IsAssignableFrom<ParameterBindingException>(error.Exception);
        Assert.StartsWith("InputObjectNotBound,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("host.get"));
    }

    [Theory]
    [InlineData("[pscustomobject]@{ HostId = '7' } | Remove-ZbxHost -Confirm:$false", "host.delete")]
    [InlineData("Import-Csv $csv | Remove-ZbxHost -Confirm:$false", "host.delete")]
    [InlineData("[pscustomobject]@{ HostId = '7' } | Set-ZbxHost -Description d -Confirm:$false", "host.update")]
    [InlineData("[pscustomobject]@{ HostId = '7' } | Disable-ZbxHost -Force -Confirm:$false", "host.update")]
    public async Task A_record_carrying_the_entity_id_binds_by_property_name_to_the_mutating_cmdlets(string script, string method)
    {
        // A CSV of ids can be piped to a change, not only to a read: ZabbixReference.TryFrom takes a PSCustomObject that
        // carries the entity's id. Without that, InputObject (ValueFromPipeline only) answers "Cannot process argument
        // transformation on parameter 'InputObject'. '' (PSCustomObject) is not a reference to a ZabbixHost".
        string csv = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(csv, "HostId,Host\n7,web-07\n", TestContext.Current.CancellationToken);
            await using FakeZabbix server = await FakeZabbix.StartAsync();
            server.On("host.get", FakeZabbix.Hosts(("7", "web-07"))).On("host.delete", """{"hostids":["7"]}""").On("host.update", """{"hostids":["7"]}""");
            using Shell shell = Shell.Open();
            shell.Connect(server);

            Run run = shell.Invoke(script, new Dictionary<string, object?> { ["csv"] = csv });

            Assert.Empty(run.Errors);
            JsonElement sent = Assert.Single(server.ParamsOf(method));
            Assert.Contains("\"7\"", sent.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(csv);
        }
    }

    [Fact]
    public async Task Piped_templates_never_have_another_templates_groups_or_links_applied_to_them()
    {
        // Each piped template's update carries its own groups and templates. Set-ZbxTemplate's -Group/-LinkedTemplate
        // are ValueFromPipelineByPropertyName and the read record has Groups/LinkedTemplates properties, so every piped
        // template rebinds them; a request built once, in EndProcessing, from whatever the parameters hold last would give
        // every template the LAST one's lists. template.update's groups/templates REPLACE the lists, so template 1 would be
        // moved into group 2 and relinked to 20.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("template.get", """
            [{"templateid":"1","host":"t1","groups":[{"groupid":"1"}],"templates":[{"templateid":"10"}]},
             {"templateid":"2","host":"t2","groups":[{"groupid":"2"}],"templates":[{"templateid":"20"}]}]
            """).On("template.update", """{"templateids":["1","2"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxTemplate -SelectGroups -SelectTemplates | Set-ZbxTemplate -Description 'reviewed' -Confirm:$false");

        Assert.Empty(run.Errors);
        foreach (JsonElement request in Assert.Single(server.ParamsOf("template.update")).EnumerateArray())
        {
            string id = request.GetProperty("templateid").GetString()!;
            if (request.TryGetProperty("groups", out JsonElement groups)) Assert.Equal(id, groups[0].GetProperty("groupid").GetString());
            // a template's templates are its children (ChildTemplates), never the templates it links (-LinkedTemplate)
            Assert.False(request.TryGetProperty("templates", out _), id);
        }
    }

    [Fact]
    public async Task A_producer_that_throws_mid_stream_sends_nothing_from_a_batching_remove_and_the_error_is_visible()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.delete", """{"hostids":[]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        RuntimeException thrown = Assert.Throws<RuntimeException>(() => shell.Invoke("& { '1'; '2'; throw 'producer failed' } | Remove-ZbxHost -Confirm:$false"));

        Assert.Empty(server.ParamsOf("host.delete"));
        Assert.Equal("producer failed", thrown.Message);
    }

    [Fact]
    public async Task A_producer_that_writes_a_non_terminating_error_mid_stream_removes_the_rest()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.delete", """{"hostids":[]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("& { '1'; Write-Error 'one bad row'; '2' } | Remove-ZbxHost -Confirm:$false");

        Assert.Equal("""["1","2"]""", Assert.Single(server.ParamsOf("host.delete")).GetRawText());
        Assert.Single(run.Errors);
    }

    [Fact]
    public async Task A_producer_that_throws_mid_stream_sends_nothing_from_a_batching_trapper_send()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        RuntimeException thrown = Assert.Throws<RuntimeException>(() => shell.Invoke("& { [pscustomobject]@{ Host = 'h'; Key = 'k'; Value = '1' }; throw 'producer failed' } | Send-ZbxData -Confirm:$false"));

        Assert.Empty(trapper.Frames);
        Assert.Equal("producer failed", thrown.Message);
    }

    [Theory]
    [InlineData("Get-ZbxHost | Select-Object -First 1")]
    [InlineData("Get-ZbxHost -Group g | Select-Object -First 1")]
    [InlineData("Get-ZbxHost | Select-Object -First 1 | ForEach-Object HostId")]
    [InlineData("Get-ZbxHost | ForEach-Object { $_ } | Select-Object -First 1")]
    public async Task Select_Object_First_downstream_of_a_get_takes_one_writes_no_error_and_asks_nothing_more(string script)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("1", "a"), ("2", "b"), ("3", "c"))).On("hostgroup.get", """[{"groupid":"5","name":"g"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        ZabbixConnection connection = shell.Context.Current!;

        Run run = shell.Invoke(script);
        shell.Invoke("Disconnect-ZbxServer");

        Assert.Empty(run.Errors);
        Assert.Single(run.Output);
        Assert.Single(server.ParamsOf("host.get"));
        Assert.True(connection.IsReleased, "the abandoned read still holds the connection's lease");
    }

    [Fact]
    public async Task Get_then_Remove_by_piped_objects_resolves_nothing_by_name()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("1", "a"), ("2", "b"))).On("host.delete", """{"hostids":["1","2"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost | Remove-ZbxHost -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Single(server.ParamsOf("host.get"));
        Assert.Equal("""["1","2"]""", Assert.Single(server.ParamsOf("host.delete")).GetRawText());
    }

    [Fact]
    public async Task WhatIf_on_a_piped_remove_sends_no_delete_and_resolves_ids_without_a_call()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("1", "a"), ("2", "b"))).On("host.delete", """{"hostids":["1","2"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost | Remove-ZbxHost -WhatIf; Get-ZbxHost | Set-ZbxHost -Description x -WhatIf; New-ZbxHostGroup -Name g -WhatIf");

        Assert.Empty(run.Errors);
        Assert.Equal(["host.get", "host.get"], server.Calls.Select(call => call.Method).Where(method => method is not ("apiinfo.version" or "user.get")));
    }
    [Fact]
    public async Task Piped_history_values_are_sent_as_one_proxy_data_page()
    {
        // Send-ZbxProxyData's -History is ValueFromPipeline, and the pipeline is gathered into the one page the synopsis
        // promises, not uploaded per record as its own page over its own connection (50 frames for 50 piped values).
        await using FakeTrapper server = FakeTrapper.Start();
        server.On("proxy data", """{"response":"success","info":"processed: 50; failed: 0; total: 50; seconds spent: 0.000100"}""");
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"1..50 | ForEach-Object {{ New-ZbxHistoryValue -ItemId 42 -Value $_ -Clock 1000 }} | Send-ZbxProxyData zbx-proxy '127.0.0.1:{server.Port}'");

        Assert.Empty(run.Errors);
        JsonElement page = Assert.Single(server.FramesOf("proxy data"));
        Assert.Equal(50, page.GetProperty("history data").GetArrayLength());
    }
}

/// <summary>A 100 000-element pipeline into the batching change cmdlets.</summary>
[Collection("Adversarial PowerShell stress")]
public sealed class AdversarialPipelineStressTests
{
    [Fact]
    public async Task A_100k_element_pipeline_into_Remove_is_one_delete_within_time_and_memory_bounds()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.delete", """{"hostids":[]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        long started = TimeProvider.System.GetTimestamp();

        Run run = shell.Invoke("1..100000 | Remove-ZbxHost -Confirm:$false");

        TimeSpan elapsed = TimeProvider.System.GetElapsedTime(started);
        long bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        Assert.Empty(run.Errors);
        Assert.Equal(100_000, Assert.Single(server.ParamsOf("host.delete")).GetArrayLength());
        Assert.True(elapsed < TimeSpan.FromSeconds(30), $"took {elapsed}");
        Assert.True(bytes < 2L * 1024 * 1024 * 1024, $"allocated {bytes / (1024 * 1024)} MiB");
    }

    [Fact]
    public async Task A_100k_element_pipeline_into_Set_is_one_update_and_one_read_back()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.update", """{"groupids":[]}""").On("hostgroup.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        long started = TimeProvider.System.GetTimestamp();

        // Set- is a sink; -PassThru is what reads the changed objects back.
        Run run = shell.Invoke("1..100000 | Set-ZbxHostGroup -Name same -PassThru -Confirm:$false");

        TimeSpan elapsed = TimeProvider.System.GetElapsedTime(started);
        Assert.Empty(run.Errors);
        Assert.Equal(100_000, Assert.Single(server.ParamsOf("hostgroup.update")).GetArrayLength());
        Assert.Single(server.ParamsOf("hostgroup.get"));
        Assert.True(elapsed < TimeSpan.FromSeconds(30), $"took {elapsed}");
    }

    [Fact]
    public async Task A_100k_element_pipeline_into_Send_ZbxData_is_sent_in_trapper_sized_batches()
    {
        // zabbix_sender sends at most VALUES_MAX = 250 values per request (src/zabbix_sender/send_buffer.h:24, 7.0), and so
        // does Send-ZbxData. One "sender data" frame carrying all 100 000 values would fail everything on one bad value or
        // one timeout, make the server's trapper hold the whole batch, and send nothing until the last record arrives.
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.Otherwise = _ => FakeTrapper.Success(1, 0);
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("1..100000 | ForEach-Object { [pscustomobject]@{ Host = 'h'; Key = 'k'; Value = \"$_\"; Clock = 1 } } | Send-ZbxData -Confirm:$false");

        Assert.Empty(run.Errors);
        int[] batches = [.. trapper.FramesOf("sender data").Select(frame => frame.GetProperty("data").GetArrayLength())];
        Assert.Equal(100_000, batches.Sum());
        Assert.All(batches, size => Assert.True(size <= 250, $"a batch of {size}: zabbix_sender sends at most 250 values per request"));
    }

    [Theory]
    [InlineData("'web-01' | Get-ZbxHost", "host.get", "host")]
    [InlineData("[pscustomobject]@{ Name = 'web-01' } | Get-ZbxHost", "host.get", "host")]
    [InlineData("'web-01' | Get-ZbxTemplate", "template.get", "host")]
    [InlineData("[pscustomobject]@{ Name = 'web-01'; Owner = 'ops' } | Get-ZbxHostGroup", "hostgroup.get", "name")]
    public async Task A_get_takes_names_from_the_pipeline_by_value_and_by_property_name(string script, string method, string nameField)
    {
        // Get-Service -Name's shape: a piped name and an Import-Csv row with a Name column select by name.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On(method, "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(script);

        Assert.True(run.Errors.All(error => error.FullyQualifiedErrorId.StartsWith("ZabbixNameNotFound,", StringComparison.Ordinal)), string.Join(" | ", run.Errors));
        JsonElement asked = Assert.Single(server.ParamsOf(method));
        Assert.Equal("""["web-01"]""", asked.GetProperty("search").GetProperty(nameField).GetRawText());
    }

    [Fact]
    public async Task A_piped_object_with_its_own_id_and_a_name_selects_by_id()
    {
        // A record carries its id and its name: the id is the identity, the default set binds it first.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("7", "web-07")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Id 7 | Get-ZbxHost");

        Assert.Empty(run.Errors);
        Assert.Equal("""["7"]""", server.ParamsOf("host.get").Last().GetProperty("hostids").GetRawText());
    }

    [Theory]
    [InlineData("'10084', '10085', '10086' | Get-ZbxHost", 1)]
    [InlineData("[pscustomobject]@{ HostId = '10084' }, [pscustomobject]@{ HostId = '10085' } | Get-ZbxHost", 1)]
    [InlineData("1..150 | Get-ZbxTemplate", 2)]
    public async Task Piped_ids_are_read_in_one_get_per_hundred(string script, int calls)
    {
        // .get takes a list of ids, as Set- and Remove- send them: twenty piped ids are one call, not twenty.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", "[]").On("template.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(script);

        Assert.Empty(run.Errors);
        Assert.Equal(calls, server.ParamsOf("host.get").Count() + server.ParamsOf("template.get").Count());
    }

    [Fact]
    public async Task A_piped_record_that_binds_more_than_its_id_is_read_on_its_own()
    {
        // A history read's piped item binds -ValueType too: records that differ by more than the id are not merged.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("history.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("[pscustomobject]@{ ItemId = '1'; ValueType = 'Float' }, [pscustomobject]@{ ItemId = '2'; ValueType = 'Text' } | Get-ZbxHistory");

        Assert.Empty(run.Errors);
        Assert.Equal(2, server.ParamsOf("history.get").Count());
    }
}
