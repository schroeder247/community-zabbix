// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// Every-parameter abuse on a representative set of cmdlets — a generated CRUD quartet
/// (<c>host</c>, <c>hostgroup</c>, <c>item</c>), hand-written ones (<c>Connect-ZbxServer</c>, <c>Send-ZbxData</c>,
/// <c>Send-ZbxHistory</c>) and an agent-host cmdlet (<c>Initialize-ZbxAgentHost</c>). A bad value must fail at binding (or
/// as one clear error record) and never reach the server; a good value must reach the wire exactly as given.
/// </summary>
public sealed class AdversarialParameterTests
{
    // Non-ASCII, astral-plane emoji, right-to-left text, a combining mark, a NUL, a tab and a newline: all ordinary
    // string data to PowerShell and to JSON.
    private const string Exotic = "Ünïcødé 😀🏳️‍🌈 עברית العربية é a\0b\tc\nd";

    private static readonly string[] InjectionLooking =
    [
        "*", "%", "'", "\"", "\\", "_", "%'--", "{\"$or\":[{\"host\":\"x\"}]}", "[1,2]", "null", "true", "42", "\\u0000", "$(Get-Date)",
    ];

    [Theory]
    [InlineData("Get-ZbxHost -Name $null")]
    [InlineData("Get-ZbxHost -Name ''")]
    [InlineData("Get-ZbxHost -Name @()")]
    [InlineData("Get-ZbxHost -Id $null")]
    [InlineData("Get-ZbxHost -Id ''")]
    [InlineData("Get-ZbxHost -Id 'web-01'")]
    [InlineData("Get-ZbxHost -Id -1")]
    [InlineData("Get-ZbxHost -Id 18446744073709551616")]
    [InlineData("Get-ZbxHost -Limit 0")]
    [InlineData("Get-ZbxHost -SortOrder Sideways")]
    [InlineData("Get-ZbxHost -Filter $null")]
    [InlineData("Remove-ZbxHost -InputObject $null -Confirm:$false")]
    [InlineData("Remove-ZbxHost -InputObject '' -Confirm:$false")]
    [InlineData("Remove-ZbxHost -InputObject '   ' -Confirm:$false")]
    [InlineData("Remove-ZbxHost -InputObject @() -Confirm:$false")]
    [InlineData("Remove-ZbxHost -InputObject @($null) -Confirm:$false")]
    [InlineData("Remove-ZbxHost -InputObject (Get-Date) -Confirm:$false")]
    [InlineData("Remove-ZbxHost -InputObject @{ hostid = '1' } -Confirm:$false")]
    [InlineData("New-ZbxItem -HostId 1 -Name n -Key k -Type NotAType -ValueType Float -Confirm:$false")]
    [InlineData("New-ZbxItem -HostId 1 -Name n -Key k -Type Zabbix -ValueType 'Float; rm -rf' -Confirm:$false")]
    [InlineData("New-ZbxItem -HostId 'x' -Name n -Key k -Type Zabbix -ValueType Float -Confirm:$false")]
    [InlineData("Set-ZbxItem -InputObject 1 -Status Paused -Confirm:$false")]
    [InlineData("Connect-ZbxServer -Uri $null")]
    [InlineData("Connect-ZbxServer -Uri 'http://127.0.0.1:1/' -ApiToken $null")]
    [InlineData("Connect-ZbxServer -Uri 'http://127.0.0.1:1/' -Name ''")]
    [InlineData("Connect-ZbxServer -Uri 'http://127.0.0.1:1/' -Name '   '")]
    [InlineData("Send-ZbxData -HostName '' -Key k -Value 1 -Confirm:$false")]
    [InlineData("Send-ZbxData -HostName h -Key $null -Value 1 -Confirm:$false")]
    [InlineData("Send-ZbxData -HostName h -Key k -Value $null -Confirm:$false")]
    [InlineData("Initialize-ZbxAgentHost -Key @() -HostName h -Confirm:$false")]
    [InlineData("Initialize-ZbxAgentHost -Key $null -HostName h -Confirm:$false")]
    [InlineData("Initialize-ZbxAgentHost -Key k -HostName '' -Confirm:$false")]
    [InlineData("Initialize-ZbxAgentHost -Key k -HostName h -InterfacePort 0 -Confirm:$false")]
    [InlineData("Initialize-ZbxAgentHost -Key k -HostName h -InterfacePort 65536 -Confirm:$false")]
    [InlineData("Initialize-ZbxAgentHost -Key k -HostName h -ValueType Bogus -Confirm:$false")]
    public async Task A_value_no_parameter_accepts_fails_at_binding_and_never_reaches_the_server(string script)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);
        int before = server.Calls.Count;

        Run run = shell.Invoke(script);

        Assert.Empty(run.Output);
        ErrorRecord error = Assert.Single(run.Errors);
        Assert.IsAssignableFrom<ParameterBindingException>(error.Exception);
        Assert.Equal(before, server.Calls.Count);
    }

    [Theory]
    [InlineData("Connect-ZbxServer -Uri 'ftp://127.0.0.1/'")]
    [InlineData("Connect-ZbxServer -Uri 'file:///etc/passwd'")]
    [InlineData("Connect-ZbxServer -Uri 'relative/path'")]
    [InlineData("Connect-ZbxServer -Uri ''")]
    public void A_url_that_is_no_http_endpoint_is_one_InvalidArgument_error_and_no_connection(string script)
    {
        // The URL is validated inside the try whose catch turns an ArgumentException into ZabbixErrors.From(failure,
        // endpoint); validated before it, the error would be 'System.ArgumentException,…ConnectZbxServerCommand',
        // category NotSpecified.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke(script);

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.Equal(ErrorCategory.InvalidArgument, error.CategoryInfo.Category);
        Assert.Null(shell.Context.Current);
    }

    [Fact]
    public async Task Ten_thousand_names_are_one_get_carrying_every_name()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Name (1..10000 | ForEach-Object { \"h$_\" })");

        // Each name that matched nothing is its own ObjectNotFound record (Get-Service's rule), after the one read.
        Assert.Equal(10000, run.Errors.Count);
        Assert.All(run.Errors, error => Assert.Equal(ErrorCategory.ObjectNotFound, error.CategoryInfo.Category));
        JsonElement get = Assert.Single(server.ParamsOf("host.get"));
        // -Name is one case-insensitive search (searchWildcardsEnabled, searchByAny).
        Assert.Equal(10000, get.GetProperty("search").GetProperty("host").GetArrayLength());
    }

    [Fact]
    public async Task Ten_thousand_ids_are_removed_in_one_delete()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.delete", """{"hostids":[]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxHost -InputObject (1..10000) -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Empty(server.ParamsOf("host.get"));
        Assert.Equal(10000, Assert.Single(server.ParamsOf("host.delete")).GetArrayLength());
    }

    [Fact]
    public async Task Exotic_text_reaches_the_wire_exactly_as_given()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.create", """{"groupids":["5"]}""").On("hostgroup.get", """[{"groupid":"5","name":"x"}]""").On("host.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHostGroup -Name $exotic; Get-ZbxHost -Name $exotic; Get-ZbxHost -Search @{ name = $exotic }",
            new Dictionary<string, object?> { ["exotic"] = Exotic });

        // The name matched no host: one ObjectNotFound naming it exactly as given.
        Assert.Equal(Exotic, Assert.Single(run.Errors).TargetObject);
        Assert.Equal(Exotic, Assert.Single(server.ParamsOf("hostgroup.create")).GetProperty("name").GetString());
        JsonElement[] gets = [.. server.ParamsOf("host.get")];
        Assert.Equal(Exotic, Strings(gets[0].GetProperty("search").GetProperty("host")).Single());
        Assert.Equal(Exotic, Strings(gets[1].GetProperty("search").GetProperty("name")).Single());
    }

    [Fact]
    public async Task A_lone_surrogate_is_refused_or_sent_escaped_never_silently_replaced()
    {
        // System.Text.Json's writer replaces a lone surrogate with U+FFFD on the way out, with no error: the server would
        // receive the replacement character and the caller's data would change silently.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.create", """{"groupids":["5"]}""").On("hostgroup.get", """[{"groupid":"5","name":"x"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        string broken = "a\uD800b";

        Run run = shell.Invoke("New-ZbxHostGroup -Name $broken", new Dictionary<string, object?> { ["broken"] = broken });

        JsonElement[] sent = [.. server.ParamsOf("hostgroup.create")];
        if (sent.Length == 0)
        {
            ErrorRecord error = Assert.Single(run.Errors);
            Assert.Equal(ErrorCategory.InvalidArgument, error.CategoryInfo.Category);
        }
        else
        {
            Assert.Equal(broken, sent[0].GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task Injection_looking_names_are_sent_as_literal_search_text()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("foreach ($n in $names) { Get-ZbxHost -Name $n }", new Dictionary<string, object?> { ["names"] = InjectionLooking.Where(n => n != "*").ToArray() });

        // Nothing matched: a literal name is an ObjectNotFound record naming it as written, a pattern (one with '*' or
        // '?') writes nothing.
        Assert.Equal(InjectionLooking.Count(n => n != "*" && !n.Contains('*', StringComparison.Ordinal) && !n.Contains('?', StringComparison.Ordinal)), run.Errors.Count);
        Assert.All(run.Errors, error => Assert.Equal(ErrorCategory.ObjectNotFound, error.CategoryInfo.Category));
        // A name is Zabbix's search with searchWildcardsEnabled, whose only wildcard is '*'
        // (zbx_db_search escapes the LIKE metacharacters '%' and '_'): every other character is sent as written.
        string?[] sent = [.. server.ParamsOf("host.get").Select(get => Strings(get.GetProperty("search").GetProperty("host")).Single())];
        Assert.Equal<string?>(InjectionLooking.Where(n => n != "*"), sent);
        Assert.All(server.ParamsOf("host.get"), get => Assert.False(get.TryGetProperty("filter", out _)));
    }

    [Fact]
    public async Task Injection_looking_Search_and_Filter_values_are_sent_as_literal_strings()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("foreach ($n in $names) { Get-ZbxHost -Search @{ host = $n }; Get-ZbxHost -Filter @{ host = $n } }",
            new Dictionary<string, object?> { ["names"] = InjectionLooking });

        Assert.Empty(run.Errors);
        JsonElement[] gets = [.. server.ParamsOf("host.get")];
        Assert.Equal(InjectionLooking.Length * 2, gets.Length);
        for (int i = 0; i < InjectionLooking.Length; i++)
        {
            Assert.Equal(InjectionLooking[i], Strings(gets[2 * i].GetProperty("search").GetProperty("host")).Single());
            Assert.Equal(InjectionLooking[i], Strings(gets[(2 * i) + 1].GetProperty("filter").GetProperty("host")).Single());
            Assert.False(gets[2 * i].TryGetProperty("searchWildcardsEnabled", out _), "a * in -Search is literal unless -SearchWildcardsEnabled");
        }
    }

    [Fact]
    public async Task Send_ZbxHistory_sends_a_string_value_as_the_string_not_as_parsed_JSON()
    {
        // Send-ZbxHistory -Value is [object]; ZabbixJsonShape.FromShell reads a top-level string as JSON TEXT, so the
        // string 'hello' fails to parse and '{"$or":1}' is sent as an object (data re-parsed into structure).
        // The parse error for 'hello' reads "'h' is an invalid start of a value. LineNumber: 0 | BytePositionInLine: 0.".
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("history.push", """{"response":"success","data":[]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            Send-ZbxHistory -HostName h -Key k -Value 'hello' -Clock 1 -Confirm:$false
            Send-ZbxHistory -HostName h -Key k -Value '{"$or":1}' -Clock 1 -Confirm:$false
            Send-ZbxHistory -HostName h -Key k -Value '42' -Clock 1 -Confirm:$false
            """);

        Assert.Empty(run.Errors);
        string?[] values = [.. server.ParamsOf("history.push").Select(push => push.EnumerateArray().Single().GetProperty("value")).Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())];
        Assert.Equal(["hello", "{\"$or\":1}", "42"], values);
        Assert.All(server.ParamsOf("history.push"), push => Assert.Equal(JsonValueKind.String, push.EnumerateArray().Single().GetProperty("value").ValueKind));
    }

    [Fact]
    public async Task Send_ZbxData_sends_exotic_and_injection_looking_values_byte_exact()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("sender data", FakeTrapper.Success(1, 0));
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");
        string[] values = [Exotic, .. InjectionLooking];

        Run run = shell.Invoke("$values | ForEach-Object { [pscustomobject]@{ Host = $exotic; Key = 'k[\"a\",b]'; Value = $_ } } | Send-ZbxData -Confirm:$false",
            new Dictionary<string, object?> { ["values"] = values, ["exotic"] = Exotic });

        Assert.Empty(run.Errors);
        JsonElement[] items = [.. Assert.Single(trapper.FramesOf("sender data")).GetProperty("data").EnumerateArray()];
        Assert.Equal(values, items.Select(item => item.GetProperty("value").GetString()));
        Assert.All(items, item => Assert.Equal(Exotic, item.GetProperty("host").GetString()));
        Assert.All(items, item => Assert.Equal("k[\"a\",b]", item.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task A_Properties_hashtable_nested_deeper_than_the_ceiling_is_one_InvalidArgument_error_and_nothing_is_sent()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.create", """{"groupids":["5"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            $deep = @{ leaf = 1 }
            foreach ($i in 1..150) { $deep = @{ n = $deep } }
            New-ZbxHostGroup -Name g -AdditionalProperty @{ a_future_api_field = $deep }
            """);

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.Equal(ErrorCategory.InvalidArgument, error.CategoryInfo.Category);
        Assert.Empty(server.ParamsOf("hostgroup.create"));
    }

    [Theory]
    [InlineData("$c = @{}; $c.self = $c; New-ZbxHostGroup -Name g -AdditionalProperty @{ a_future_api_field = $c } -Confirm:$false")]
    [InlineData("$c = @{}; $c.self = $c; New-ZbxHostGroup -Name g -AdditionalProperty $c -Confirm:$false")]
    [InlineData("$c = @{ ip = '1.1.1.1' }; $c.details = $c; New-ZbxHost -Host h -Group 1 -AdditionalProperty @{ interfaces = @($c) } -Confirm:$false")]
    [InlineData("$c = @{}; $c.filter = $c; Get-ZbxHost -Filter $c")]
    public async Task A_cyclic_value_is_one_error_record_not_a_hang_or_a_crash(string script)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.create", """{"groupids":["5"]}""").On("host.create", """{"hostids":["5"]}""").On("host.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        int before = server.Calls.Count;

        Run run = await Task.Run(() => shell.Invoke(script), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Single(run.Errors);
        Assert.Equal(before, server.Calls.Count);
    }

    [Fact]
    public async Task An_invalid_enum_name_inside_Properties_is_an_InvalidArgument_error_naming_the_field()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.create", """{"itemids":["5"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxItem -HostId 1 -Name n -Key k -ValueType Float -AdditionalProperty @{ type = 'NotAType' } -Confirm:$false");

        // The JsonException of the merge is the caller's own input, so it is InvalidArgument, not InvalidResult (which
        // ZabbixErrors uses for a JsonException meaning "the server answered badly").
        ErrorRecord error = Assert.Single(run.Errors);
        Assert.Equal(ErrorCategory.InvalidArgument, error.CategoryInfo.Category);
        Assert.Contains("type", error.Exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(server.ParamsOf("item.create"));
    }

    [Fact]
    public async Task An_undefined_numeric_enum_value_fails_at_binding()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.create", """{"itemids":["5"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxItem -HostId 1 -Name n -Key k -Type 999 -ValueType Float -Confirm:$false");

        Assert.IsAssignableFrom<ParameterBindingException>(Assert.Single(run.Errors).Exception);
        Assert.Empty(server.ParamsOf("item.create"));
    }

    [Fact]
    public async Task Initialize_ZbxAgentHost_creates_one_item_per_distinct_key()
    {
        // A repeated key must not send item.create twice: against a real server the second create fails after the host
        // and the first item exist, and the failed operation's record (the host it created) is never kept, so
        // Clear-ZbxAgentHost cannot remove them.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", "[]").On("hostgroup.get", """[{"groupid":"50","name":"PowerShell agents"}]""")
              .On("host.create", """{"hostids":["100"]}""").On("hostinterface.get", """[{"interfaceid":"10","hostid":"100","type":1}]""")
              .On("item.get", "[]");
        int next = 1;
        server.On("item.create", _ => $$"""{"itemids":["{{next++}}"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Initialize-ZbxAgentHost -Key agent.ping, agent.ping, AGENT.PING -HostName h -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Equal(["agent.ping", "AGENT.PING"], server.ParamsOf("item.create").Select(create => create.GetProperty("key_").GetString()));
    }

    [Theory]
    [InlineData("Get-ZbxHost -Search @{ host = @(,@('a','b')) }")]
    [InlineData("Get-ZbxHost -Search @{ host = @{ x = 1 } }")]
    [InlineData("Get-ZbxHost -Search @{ host = (Get-Date) }")]
    [InlineData("$c = [System.Collections.ArrayList]::new(); [void]$c.Add($c); Get-ZbxHost -Search @{ host = $c }")]
    public async Task A_Search_value_that_is_no_string_or_flat_list_of_strings_is_refused_never_sent_as_a_type_name(string script)
    {
        // ToString() on every element would ask the server to search host for "System.Object[]",
        // "System.Collections.DictionaryEntry", a culture-formatted date, and "System.Collections.ArrayList": no error,
        // and a result set for a term nobody wrote.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(script);

        Assert.Single(run.Errors);
        Assert.All(server.ParamsOf("host.get"), get => Assert.DoesNotContain("System.", get.GetRawText(), StringComparison.Ordinal));
        Assert.Empty(server.ParamsOf("host.get"));
    }

    [Fact]
    public async Task Send_ZbxData_sends_an_empty_string_value_as_zabbix_sender_does()
    {
        // A Mandatory string refuses '' unless [AllowEmptyString] ("Cannot bind argument to parameter 'Value' because it
        // is an empty string."); zabbix_sender -o '' sends it (zabbix_sender.c has no such check)
        // and a text/log item takes it.
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("sender data", FakeTrapper.Success(1, 0));
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("Send-ZbxData web-01 log.key '' -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Equal(string.Empty, Assert.Single(trapper.FramesOf("sender data")).GetProperty("data").EnumerateArray().Single().GetProperty("value").GetString());
    }

    internal static IEnumerable<string?> Strings(JsonElement value)
        => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(v => v.GetString()) : [value.GetString()];
}
