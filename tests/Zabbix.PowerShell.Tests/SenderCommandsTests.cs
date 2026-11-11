// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The sender: <c>Send-ZbxData</c>.</summary>
public sealed class SenderCommandsTests
{
    [Fact]
    public async Task Send_ZbxData_sends_one_value_to_the_connections_trapper()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("sender data", FakeTrapper.Success(1, 0));
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("Send-ZbxData web-01 agent.ping 1 -Confirm:$false");

        Assert.Empty(run.Errors);
        var response = Assert.IsType<ZabbixSenderResponse>(Assert.Single(run.Output).BaseObject);
        Assert.Equal(1, response.Processed);
        JsonElement sent = Assert.Single(trapper.FramesOf("sender data"));
        JsonElement item = Assert.Single(sent.GetProperty("data").EnumerateArray());
        Assert.Equal("web-01", item.GetProperty("host").GetString());
        Assert.Equal("agent.ping", item.GetProperty("key").GetString());
        Assert.Equal("1", item.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Send_ZbxData_batches_piped_records_into_one_request()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("sender data", FakeTrapper.Success(2, 0));
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("""
            @(
                [pscustomobject]@{ Host = 'web-01'; Key = 'agent.ping'; Value = '1' }
                [pscustomobject]@{ Host = 'web-02'; Key = 'agent.ping'; Value = '1' }
            ) | Send-ZbxData -Confirm:$false
            """);

        Assert.Empty(run.Errors);
        var response = Assert.IsType<ZabbixSenderResponse>(Assert.Single(run.Output).BaseObject);
        Assert.Equal(2, response.Processed);
        JsonElement sent = Assert.Single(trapper.FramesOf("sender data"));
        Assert.Equal(2, sent.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task WhatIf_sends_nothing()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("sender data", FakeTrapper.Success(1, 0));
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("Send-ZbxData web-01 agent.ping 1 -WhatIf");

        Assert.Empty(run.Output);
        Assert.Empty(trapper.FramesOf("sender data"));
    }

    [Fact]
    public async Task Values_sent_without_a_clock_never_share_an_instant()
    {
        // Stamped at the whole second (ns 0), a second value for the same item in that second would collide on history's
        // (itemid, clock, ns) key and be skipped while the reply says processed (on 7.0.31).
        // On a frozen clock every value of the pipeline, and the next command's, still gets its own instant.
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_790_000_000).AddTicks(1234));
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("sender data", FakeTrapper.Success(3, 0));
        using Shell shell = Shell.Open();
        shell.Context.Time = clock;
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("""
            'a', 'b', 'c' | ForEach-Object { [pscustomobject]@{ Host = 'web-01'; Key = 'app.status'; Value = $_ } } | Send-ZbxData -Confirm:$false
            """);

        Assert.Empty(run.Errors);
        (long Clock, long Ns)[] stamps = [.. Assert.Single(trapper.FramesOf("sender data")).GetProperty("data").EnumerateArray()
            .Select(value => (value.GetProperty("clock").GetInt64(), value.GetProperty("ns").GetInt64()))];
        Assert.Equal(3, stamps.Distinct().Count());
        Assert.Equal((1_790_000_000L, 123_400L), stamps[0]);
    }
}
