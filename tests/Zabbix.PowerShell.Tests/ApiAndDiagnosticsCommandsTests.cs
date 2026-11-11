// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The raw API door and error buffer: <c>Invoke-ZbxApi</c>, <c>Get-ZbxError</c>.</summary>
public sealed class ApiAndDiagnosticsCommandsTests
{
    [Fact]
    public async Task Invoke_ZbxApi_sends_the_bodys_shell_shape_and_shapes_the_result_back()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"10084","host":"web-01"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""Invoke-ZbxApi host.get @{ output = 'extend' }""");

        Assert.Empty(run.Errors);
        PSObject host = Assert.Single(run.Output);
        Assert.Equal("10084", host.Properties["hostid"].Value);
        JsonElement asked = Assert.Single(server.ParamsOf("host.get"));
        Assert.Equal("extend", asked.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Invoke_ZbxApi_with_no_Body_sends_an_empty_object_not_null()
    {
        // apiinfo.version with params null answers -32600 'Invalid parameter "/params": an array or object
        // is expected' (measured on 7.0 and 8.0).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("apiinfo.version", "\"7.0.31\"");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Invoke-ZbxApi apiinfo.version");

        Assert.Empty(run.Errors);
        Assert.Equal("{}", server.ParamsOf("apiinfo.version").Last().GetRawText());
    }

    [Fact]
    public async Task A_get_method_skips_the_confirmation_gate_a_mutation_does_not()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", "[]");
        server.On("host.delete", """{"hostids":["1"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run read = shell.Invoke("""Invoke-ZbxApi host.get @{} -Confirm:$false -WhatIf""");
        Assert.Single(server.ParamsOf("host.get"));                // -WhatIf does not gate a read

        Run write = shell.Invoke("""Invoke-ZbxApi host.delete @('1') -WhatIf""");
        Assert.Empty(server.ParamsOf("host.delete"));               // -WhatIf gates a mutation
    }

    [Fact]
    public async Task Get_ZbxError_reads_the_engines_own_Error_filtered_to_Zabbix_failures()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Error(-32602, "Invalid params.", "No permissions."));
        using Shell shell = Shell.Open();
        shell.Connect(server);
        shell.Invoke("Get-ZbxHost");
        shell.Invoke("Write-Error 'not ours' -ErrorId TotallyUnrelated");

        Run run = shell.Invoke("Get-ZbxError");

        ErrorRecord error = Assert.Single(run.Output).BaseObject as ErrorRecord ?? throw new InvalidOperationException();
        Assert.StartsWith("ZabbixApi.", error.FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ZbxError_Last_limits_the_count_newest_first()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Error(-32602, "one.", "detail"));
        using Shell shell = Shell.Open();
        shell.Connect(server);
        shell.Invoke("Get-ZbxHost");
        server.On("host.get", FakeZabbix.Error(-32602, "two.", "detail"));
        shell.Invoke("Get-ZbxHost");

        Run run = shell.Invoke("Get-ZbxError -Last 1");

        var error = Assert.Single(run.Output).BaseObject as ErrorRecord;
        Assert.Contains("two.", error!.Exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_ZbxError_returns_a_wire_failure_of_the_module()
    {
        // A refused connection's exception is a SocketException; the command that wrote it, not its text, makes it a
        // Zabbix failure. The default timeout, because Windows retries a refused connection for two seconds before reporting it.
        using Shell shell = Shell.Open();
        shell.Invoke("Get-ZbxAgentItem -Agent 127.0.0.1:1 -Key agent.ping");

        Run run = shell.Invoke("Get-ZbxError");

        ErrorRecord error = Assert.Single(run.Output).BaseObject as ErrorRecord ?? throw new InvalidOperationException();
        Assert.StartsWith("ZabbixWire.ConnectionRefused,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Equal("connection_refused", error.Exception.Data[ZabbixErrors.ErrorTypeKey]);
    }

    [Fact]
    public void One_wire_failure_writes_one_record_and_no_warning()
    {
        // The getter's own narrative of the failure (event 1201) and the error record say the same thing once: the
        // narrative is held, and written only with -Verbose when the operation fails with it.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Get-ZbxAgentItem -Agent 127.0.0.1:1 -Key agent.ping -Timeout 1");
        Run verbose = shell.Invoke("Get-ZbxAgentItem -Agent 127.0.0.1:1 -Key agent.ping -Timeout 1 -Verbose");

        Assert.Single(run.Errors);
        Assert.Empty(run.Warnings);
        Assert.Contains(verbose.Verbose, line => line.Contains("Agent 127.0.0.1:1 could not be asked", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void Every_error_id_follows_one_scheme(Exception failure, string id)
    {
        Assert.Equal(id, ZabbixErrors.IdOf(failure));
        Assert.Matches(ErrorIdPattern, id);
    }

    /// <summary>One scheme: <c>Zabbix&lt;Layer&gt;.&lt;Kind&gt;</c>, or one PascalCase word for the module's own rules.</summary>
    internal const string ErrorIdPattern = "^Zabbix[A-Za-z]*(\\.[A-Z][A-Za-z0-9]*)?$";

    public static TheoryData<Exception, string> Failures() => new()
    {
        { new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused), "ZabbixWire.ConnectionRefused" },
        { new IOException("reset", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset)), "ZabbixWire.ConnectionReset" },
        { new System.Security.Authentication.AuthenticationException("handshake"), "ZabbixWire.TlsHandshakeFailed" },
        { new InvalidDataException("frame"), "ZabbixWire.InvalidFrame" },
        { new HttpRequestException(HttpRequestError.ConnectionError, "refused"), "ZabbixHttp.ConnectionError" },
        { new HttpRequestException("denied", null, System.Net.HttpStatusCode.Forbidden), "ZabbixHttp.Forbidden" },
        { new JsonException("bad"), "ZabbixApi.InvalidResponse" },
        { new ZabbixParameterException("ZabbixReferenceNotFound", "Host", "no host"), "ZabbixReferenceNotFound" },
        { new TimeoutException("late"), "Zabbix.Timeout" },
        { new InvalidOperationException("no"), "Zabbix.InvalidOperation" },
    };
}
