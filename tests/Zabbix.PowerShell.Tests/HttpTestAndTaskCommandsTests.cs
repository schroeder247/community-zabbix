// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// <c>New-ZbxHttpTest</c>/<c>Set-ZbxHttpTest</c>/<c>Remove-ZbxHttpTest</c> and
/// <c>New-ZbxTask</c>, hand-written because their entity's <c>get</c> parameters record cannot implement
/// <see cref="IZabbixGetParameters"/> (no <c>searchWildcardsEnabled</c>/<c>filter</c>+<c>search</c> respectively), so
/// the generator skips them.
/// </summary>
public sealed class HttpTestAndTaskCommandsTests
{
    [Fact]
    public async Task New_ZbxHttpTest_creates_and_reads_the_scenario_back_with_a_typed_step()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("httptest.create", """{"httptestids":["2"]}""");
        server.On("httptest.get", """[{"httptestid":"2","name":"login"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            $step = New-ZbxHttpTestStep -Name Homepage -Url 'http://localhost/' -StatusCodes 200 -StepNumber 1
            New-ZbxHttpTest -Name login -Host web-01 -Step @($step) -Confirm:$false
            """);

        Assert.Empty(run.Errors);
        dynamic created = Assert.Single(run.Output);
        Assert.Equal("2", created.HttpTestId.ToString());
        JsonElement request = Assert.Single(server.ParamsOf("httptest.create"));
        Assert.Equal("login", request.GetProperty("name").GetString());
        Assert.Equal("10084", request.GetProperty("hostid").GetString());
        JsonElement step = Assert.Single(request.GetProperty("steps").EnumerateArray());
        Assert.Equal("Homepage", step.GetProperty("name").GetString());
        Assert.Equal("200", step.GetProperty("status_codes").GetString());
    }

    [Fact]
    public void New_ZbxHttpTestStep_builds_a_step_without_its_number()
    {
        // The step number (-StepNumber) is optional; httptest.create requires "no" (CHttpTest.php
        // 7.0:304, 8.0:394), and New-ZbxHttpTest supplies it from the step's position.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("New-ZbxHttpTestStep -Name 'Homepage' -Url 'http://localhost/' -StatusCodes '200'");

        Assert.Empty(run.Errors);
        dynamic step = Assert.Single(run.Output);
        Assert.Equal("Homepage", step.name);
        Assert.Equal("200", step.status_codes);
        Assert.False(((System.Collections.IDictionary)((System.Management.Automation.PSObject)step).BaseObject).Contains("no"));
    }

    [Fact]
    public async Task New_ZbxHttpTest_numbers_the_steps_that_name_no_number_by_their_position()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("httptest.create", """{"httptestids":["2"]}""");
        server.On("httptest.get", """[{"httptestid":"2","name":"login"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            $steps = (New-ZbxHttpTestStep Home 'http://localhost/'),
                     @{ name = 'Login'; url = 'http://localhost/login' },
                     [pscustomobject]@{ name = 'Out'; url = 'http://localhost/out' },
                     (New-ZbxHttpTestStep Last 'http://localhost/last' -StepNumber 9)
            New-ZbxHttpTest -Name login -Host web-01 -Step $steps -Confirm:$false
            """);

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("httptest.create"));
        Assert.Equal(
            ["Home=1", "Login=2", "Out=3", "Last=9"],
            request.GetProperty("steps").EnumerateArray().Select(step => $"{step.GetProperty("name").GetString()}={step.GetProperty("no").GetInt32()}"));
    }

    [Fact]
    public async Task New_ZbxHttpTest_Steps_accepts_a_hashtable_keyed_by_wire_names()
    {
        // The raw httptest.create calling convention: @{name=...; url=...; status_codes=...; no=...}.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("httptest.create", """{"httptestids":["2"]}""");
        server.On("httptest.get", """[{"httptestid":"2","name":"login"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            New-ZbxHttpTest -Name login -Host web-01 -Step @(@{name='Check Index'; url='http://localhost/'; status_codes='200'; no=1}) -Confirm:$false
            """);

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("httptest.create"));
        JsonElement step = Assert.Single(request.GetProperty("steps").EnumerateArray());
        Assert.Equal("Check Index", step.GetProperty("name").GetString());
        Assert.Equal(1, step.GetProperty("no").GetInt32());
    }

    [Theory]
    [InlineData("New-ZbxHttpTest", typeof(ZabbixHttpTestStepRequest[]))]
    [InlineData("Set-ZbxHttpTest", typeof(ZabbixHttpTestUpdateStepRequest[]))]
    public void The_Steps_of_a_web_scenario_are_the_requests_own_step_records(string command, Type expected)
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"(Get-Command {command}).Parameters['Step'].ParameterType");

        Assert.Empty(run.Errors);
        Assert.Equal(expected, Assert.Single(run.Output).BaseObject);
    }

    [Theory]
    [InlineData("New-ZbxHttpTest -Name login -Host web-01 -Step 'not a step' -Confirm:$false")]
    [InlineData("New-ZbxHttpTest -Name login -Host web-01 -Step @{ name = 'Home'; no = 'first' } -Confirm:$false")]
    [InlineData("Set-ZbxHttpTest -Id 2 -Step @{ httpstepid = @(1, 2) } -Confirm:$false")]
    public async Task A_step_that_does_not_shape_the_request_record_is_a_binding_error(string command)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(command);

        Assert.StartsWith("ParameterArgumentTransformationError,", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.DoesNotContain(server.Calls, call => call.Method.StartsWith("httptest.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Set_ZbxHttpTest_Steps_takes_the_builders_output_as_update_steps()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("httptest.update", """{"httptestids":["2"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxHttpTest -Id 2 -Step (New-ZbxHttpTestStep Home 'http://localhost/' -StepNumber 1), @{ httpstepid = '7'; url = 'http://localhost/new' } -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement[] steps = [.. Assert.Single(server.ParamsOf("httptest.update")).GetProperty("steps").EnumerateArray()];
        Assert.Equal("Home", steps[0].GetProperty("name").GetString());
        Assert.Equal("7", steps[1].GetProperty("httpstepid").GetString());
        Assert.False(steps[1].TryGetProperty("name", out _));
    }

    [Fact]
    public async Task New_ZbxHttpTest_sends_the_ssl_variables_headers_and_tags_fields()
    {
        // Every httptest.create field is a typed parameter; the pair lists take the flat hashtable.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("httptest.create", """{"httptestids":["2"]}""");
        server.On("httptest.get", """[{"httptestid":"2","name":"login"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            New-ZbxHttpTest -Name login -Host web-01 -Step @(@{ name = 'Home'; url = 'http://localhost/'; no = 1 }) `
                -SslCertFile cert.pem -SslKeyFile key.pem -SslKeyPassword (ConvertTo-SecureString 'k3y' -AsPlainText -Force) `
                -Variable @{ '{user}' = 'admin' } -Header ([ordered]@{ Accept = 'text/html'; 'X-Trace' = '1' }) -Tag @{ env = 'lab' } `
                -Confirm:$false | Out-Null
            """);

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("httptest.create"));
        Assert.Equal("cert.pem", request.GetProperty("ssl_cert_file").GetString());
        Assert.Equal("key.pem", request.GetProperty("ssl_key_file").GetString());
        Assert.Equal("k3y", request.GetProperty("ssl_key_password").GetString());
        Assert.Equal("""[{"name":"{user}","value":"admin"}]""", request.GetProperty("variables").GetRawText());
        Assert.Equal("""[{"name":"Accept","value":"text/html"},{"name":"X-Trace","value":"1"}]""", request.GetProperty("headers").GetRawText());
        Assert.Equal("""[{"tag":"env","value":"lab"}]""", request.GetProperty("tags").GetRawText());
    }

    [Fact]
    public async Task Set_ZbxHttpTest_sends_the_proxy_variables_headers_and_tags_fields()
    {
        // Set- carries -HttpProxy, -Variable, -Header and -Tag, as New- does.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("httptest.update", """{"httptestids":["2"]}""");
        server.On("httptest.get", """[{"httptestid":"2","name":"login"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            Set-ZbxHttpTest -Id 2 -HttpProxy 'http://proxy:3128' -Variable @{ '{user}' = 'admin' } `
                -Header @{ Accept = 'text/html' } -Tag @{ env = 'lab' } -Confirm:$false | Out-Null
            """);

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("httptest.update"));
        Assert.Equal("http://proxy:3128", request.GetProperty("http_proxy").GetString());
        Assert.Equal("""[{"name":"{user}","value":"admin"}]""", request.GetProperty("variables").GetRawText());
        Assert.Equal("""[{"name":"Accept","value":"text/html"}]""", request.GetProperty("headers").GetRawText());
        Assert.Equal("""[{"tag":"env","value":"lab"}]""", request.GetProperty("tags").GetRawText());
    }

    [Fact]
    public async Task Set_ZbxHttpTest_renames_by_id_and_reads_back()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("httptest.update", """{"httptestids":["2"]}""");
        server.On("httptest.get", """[{"httptestid":"2","name":"login-renamed"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxHttpTest -Id 2 -Name login-renamed -Confirm:$false -PassThru");

        Assert.Empty(run.Errors);
        dynamic updated = Assert.Single(run.Output);
        Assert.Equal("login-renamed", updated.Name);
        JsonElement request = Assert.Single(server.ParamsOf("httptest.update"));
        Assert.Equal("2", request.GetProperty("httptestid").GetString());
        Assert.Equal("login-renamed", request.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Remove_ZbxHttpTest_deletes_by_id()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("httptest.delete", """{"httptestids":["2"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxHttpTest -Id 2 -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("httptest.delete"));
        Assert.Equal("2", Assert.Single(request.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task New_ZbxTask_creates_a_check_now_task_and_reads_it_back()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("task.create", """{"taskids":["9"]}""");
        server.On("task.get", """[{"taskid":"9","status":"1"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            $request = [Community.Zabbix.ZabbixTaskRequest]::new()
            $itemProp = [Community.Zabbix.ZabbixTaskRequest].GetProperty('ItemId')
            $itemProp.SetValue($request, [ulong]'100084')
            New-ZbxTask -Type CheckNow -Request $request -Confirm:$false
            """);

        Assert.Empty(run.Errors);
        dynamic created = Assert.Single(run.Output);
        Assert.Equal("9", created.TaskId.ToString());
        JsonElement request = Assert.Single(server.ParamsOf("task.create"));
        Assert.Equal("100084", request.GetProperty("request").GetProperty("itemid").GetString());
        Assert.Equal("6", request.GetProperty("type").GetRawText());
    }
}
