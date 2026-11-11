// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Security;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// Connect-ZbxServer proves its credential before it adds the connection; an unreachable server is one error, its
/// tries verbose; New-ZbxToken -Generate hands back the secret as a SecureString.
/// </summary>
public sealed class ConnectCredentialTests
{
    private const string Refused = """{"error":{"code":-32602,"message":"Invalid params.","data":"Session terminated, re-login, please."}}""";

    [Fact]
    public async Task The_frontend_s_address_connects_to_its_api_jsonrpc_php_by_the_SDK_s_rule()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Connect-ZbxServer -Uri '{server.BaseAddress}' -ApiToken (ConvertTo-SecureString 'test-token' -AsPlainText -Force)");

        Assert.Empty(run.Errors);
        Assert.Equal(server.Endpoint, shell.Context.Current!.Client.Endpoint);   // ZabbixOptions.GetApiEndpoint, as the SDK reads it
        Assert.Contains(server.Calls, call => call.Method == "user.get");
    }

    [Fact]
    public async Task A_token_the_server_does_not_know_ends_Connect_and_adds_nothing()
    {
        // apiinfo.version takes no token, so it cannot prove one; without a real call the first command would say only
        // "Session terminated, re-login, please." and the shell would hold a connection that can never work.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("user.get", Refused);
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Connect-ZbxServer -Uri '{server.Endpoint}' -ApiToken (ConvertTo-SecureString 'wrong-token' -AsPlainText -Force); 'after'");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("ZabbixApi.SessionTerminated,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Equal(ErrorCategory.AuthenticationError, error.CategoryInfo.Category);
        Assert.Contains("refused the API token", error.ErrorDetails.Message, StringComparison.Ordinal);
        Assert.Contains(server.Endpoint.Authority, error.ErrorDetails.Message, StringComparison.Ordinal);
        Assert.Contains("New-ZbxToken -Generate", error.ErrorDetails.RecommendedAction, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-token", error.ErrorDetails.Message, StringComparison.Ordinal);
        Assert.Empty(run.Warnings);                                         // the API's answer is the error, not also a warning
        Assert.Equal(["after"], run.Output.Select(o => o.BaseObject));      // statement-terminating: the script goes on
        Assert.Empty(shell.Context.Servers);
        Assert.Equal("Bearer wrong-token", server.Calls.Single(call => call.Method == "user.get").Authorization);
    }

    [Fact]
    public async Task A_refused_credential_is_caught_as_a_terminating_error()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("user.get", Refused);
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"try {{ Connect-ZbxServer -Uri '{server.Endpoint}' -ApiToken (ConvertTo-SecureString 't' -AsPlainText -Force) }} catch [Community.Zabbix.ZabbixApiException] {{ $_.Exception.Kind.ToString() }}");

        Assert.Equal("SessionTerminated", Assert.Single(run.Output).BaseObject);
        Assert.Empty(run.Errors);
    }

    [Fact]
    public async Task A_wrong_password_ends_Connect_with_LoginFailed()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("user.login", """{"error":{"code":-32500,"message":"Application error.","data":"Incorrect user name or password or account is temporarily blocked."}}""");
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Connect-ZbxServer -Uri '{server.Endpoint}' -Credential ([pscredential]::new('Admin', (ConvertTo-SecureString 'wrong' -AsPlainText -Force)))");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("ZabbixApi.LoginFailed,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Equal(ErrorCategory.AuthenticationError, error.CategoryInfo.Category);
        Assert.Contains("refused the user name and password", error.ErrorDetails.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Admin", error.ErrorDetails.Message, StringComparison.Ordinal);
        Assert.Empty(run.Warnings);
        Assert.Empty(shell.Context.Servers);
    }

    [Fact]
    public async Task A_good_credential_is_proven_once_and_a_role_without_user_get_still_connects()
    {
        // Zabbix authenticates before it checks the role, so Permission proves the token good.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("user.get", """{"error":{"code":-32500,"message":"Application error.","data":"No permissions to call \"user.get\"."}}""");
        using Shell shell = Shell.Open();

        Run run = shell.Connect(server);

        Assert.Empty(run.Errors);
        Assert.Single(shell.Context.Servers);
        Assert.Single(server.Calls, call => call.Method == "user.get");
    }

    [Fact]
    public async Task An_anonymous_connect_proves_nothing_and_still_connects()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("user.get", Refused);
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Connect-ZbxServer -Uri '{server.Endpoint}'");

        Assert.Empty(run.Errors);
        Assert.Single(shell.Context.Servers);
        Assert.DoesNotContain(server.Calls, call => call.Method == "user.get");
    }

    [Fact]
    public void An_unreachable_server_is_one_error_and_its_tries_are_verbose_not_warnings()
    {
        // Polly logs each try under its own category, at Warning or Error: they must not print one WARNING line per try
        // ahead of the error that says the server cannot be reached.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Connect-ZbxServer -Uri 'http://127.0.0.1:1/' -ApiToken (ConvertTo-SecureString 't' -AsPlainText -Force) -TimeoutSec 5 -Verbose");

        // Refused at every try, then the deadline: ConnectionError or OperationTimeout, whichever comes first.
        Assert.Single(run.Errors);
        Assert.DoesNotContain(run.Warnings, warning => warning.Contains("Execution attempt", StringComparison.Ordinal));
        Assert.Contains(run.Verbose, line => line.StartsWith("Execution attempt.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task New_ZbxToken_Generate_adds_the_secret_as_a_SecureString_and_never_shows_it()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("token.create", """{"tokenids":["7"]}""")
            .On("token.get", """[{"tokenid":"7","name":"Inventory reader","userid":"1","status":"0"}]""")
            .On("token.generate", """[{"tokenid":"7","token":"0123456789abcdef"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("$t = New-ZbxToken -Name 'Inventory reader' -Generate; $t; $t | Out-String; [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($t.Secret))");

        Assert.Empty(run.Errors);
        Assert.IsType<ZabbixToken>(run.Output[0].BaseObject);
        Assert.IsType<SecureString>(run.Output[0].Properties["Secret"].Value);
        Assert.DoesNotContain("0123456789abcdef", run.Output[1].BaseObject.ToString(), StringComparison.Ordinal);
        Assert.Equal("0123456789abcdef", run.Output[2].BaseObject);
        Assert.Equal("7", server.ParamsOf("token.generate").Single()[0].GetString());
    }

    [Fact]
    public async Task A_name_that_matches_no_host_group_says_host_group_in_words()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", "[]").On("host.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Group 'No such group'");

        ErrorRecord error = Assert.Single(run.Errors, record => record.FullyQualifiedErrorId.StartsWith("ZabbixReferenceNotFound,", StringComparison.Ordinal));
        Assert.StartsWith("no host group is named 'No such group'", error.Exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_ZbxToken_without_Generate_asks_for_no_secret()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("token.create", """{"tokenids":["7"]}""").On("token.get", """[{"tokenid":"7","name":"Inventory script"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxToken -Name 'Inventory script'");

        Assert.Empty(run.Errors);
        Assert.Null(Assert.Single(run.Output).Properties["Secret"]);
        Assert.DoesNotContain(server.Calls, call => call.Method == "token.generate");
    }
}
