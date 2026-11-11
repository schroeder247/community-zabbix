// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The error contract, and confirmation through the engine.</summary>
public sealed class ErrorAndConfirmationTests
{
    [Fact]
    public async Task An_api_error_is_one_error_record_named_by_its_kind()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Error(-32500, "Application error.", "No permissions to call \\\"host.get\\\"."));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.Equal("ZabbixApi.Permission,Community.Zabbix.PowerShell.Commands.GetZbxHostCommand", error.FullyQualifiedErrorId);
        Assert.Equal(ErrorCategory.PermissionDenied, error.CategoryInfo.Category);
        Assert.IsType<ZabbixApiException>(error.Exception);
    }

    [Fact]
    public async Task An_api_error_answered_to_a_get_is_its_own_error_and_no_warning_for_the_next_command()
    {
        // The call starts inside the operation, with this command's pipeline current: the API's error answer (logged
        // at Warning as it arrives) belongs to Get-ZbxHost and folds into its one error record, instead of waiting in
        // the connection's buffer for whichever command runs next.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Error(-32500, "Application error.", "No permissions to call \\\"host.get\\\"."));
        server.On("hostgroup.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost; Get-ZbxHostGroup");

        Assert.Single(run.Errors);
        Assert.Empty(run.Warnings);
    }

    [Fact]
    public async Task A_get_writes_its_own_request_to_the_debug_stream()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Debug");

        Assert.Contains(run.Debug, line => line.StartsWith("JSON-RPC host.get #", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_database_error_is_an_invalid_operation_whose_text_says_the_method_and_the_detail_once()
    {
        // ZabbixApiException.Message is "method: message detail" and -32500 "Database error occurred." is its
        // own kind. The record's text is the exception's, with no repeat of the method and the detail in ErrorDetails.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Error(-32500, "Application error.", "Database error occurred."));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("ZabbixApi.Database,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Equal(ErrorCategory.InvalidOperation, error.CategoryInfo.Category);
        Assert.Equal("host.get: Application error. Database error occurred.", error.ToString());
        Assert.Null(error.ErrorDetails);
    }

    [Theory]
    [InlineData(typeof(TimeoutException), ErrorCategory.OperationTimeout)]
    [InlineData(typeof(InvalidDataException), ErrorCategory.InvalidResult)]
    [InlineData(typeof(ArgumentException), ErrorCategory.InvalidArgument)]
    [InlineData(typeof(ObjectDisposedException), ErrorCategory.ResourceUnavailable)]
    public void A_failure_maps_to_the_category_of_its_kind(Type exception, ErrorCategory category)
    {
        var failure = (Exception)Activator.CreateInstance(exception, "x")!;

        ErrorRecord record = ZabbixErrors.From(failure);

        Assert.Equal(category, record.CategoryInfo.Category);
        Assert.Equal(ZabbixErrors.IdOf(failure), record.FullyQualifiedErrorId);
    }

    [Fact]
    public async Task Remove_prompts_by_default_so_a_host_that_cannot_prompt_removes_nothing()
    {
        // The generated Remove- carries ConfirmImpact.High and calls
        // Cmdlet.ShouldProcess alone (no second, ShouldContinue-based confirmation for an ordinary delete);
        // ConfirmImpact.High is at or above the
        // engine's default $ConfirmPreference (High), so ShouldProcess itself tries to prompt. This runspace's host
        // cannot prompt, so the attempt fails and nothing is deleted.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.delete", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxTestHost 10084");

        Assert.NotEmpty(run.Errors);
        Assert.Empty(server.ParamsOf("host.delete"));
    }

    [Fact]
    public async Task A_host_that_cannot_prompt_fails_ShouldChange_with_a_catchable_error_not_a_null_reference()
    {
        // ShouldChange (kept for the hand-written commands that still batch many objects or ask a second
        // ShouldContinue question — Membership/Tags/History/Import-Configuration, Invoke-ZbxTestShouldChange here stands
        // in for them) must not let Cmdlet.ShouldContinue's own exception (a bare NullReferenceException, from
        // Microsoft.PowerShell.ConsoleHostUserInterface.PromptForChoice, in a real console host with no input to
        // read) propagate uncaught out of the cmdlet. This test's runspace has no interactive host either, so
        // ShouldContinue fails the same way here; ShouldChange must turn that into one ordinary, catchable
        // ZabbixConfirmationUnavailable error, matching every other failure this module reports, never an
        // uncaught .NET exception the caller cannot catch by type or id.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Invoke-ZbxTestShouldChange");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("ZabbixConfirmationUnavailable,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Empty(run.Output);
    }

    [Fact]
    public void Invoke_ZbxTestShouldChange_Confirm_false_skips_the_second_confirmation_without_Force()
    {
        // The usual script convention (-Confirm:$false, no -Force) must not need a host that can prompt:
        // declining the second confirmation explicitly is not the same as never asking, so it must not throw even
        // where the bare non-interactive-host test (above) fails for lack of a prompt.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Invoke-ZbxTestShouldChange -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.True((bool)Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public void Invoke_ZbxTestShouldChange_Force_skips_the_second_confirmation()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Invoke-ZbxTestShouldChange -Force");

        Assert.Empty(run.Errors);
        Assert.True((bool)Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public void Invoke_ZbxTestShouldChange_WhatIf_changes_nothing_even_with_Force()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Invoke-ZbxTestShouldChange -Force -WhatIf");

        Assert.Empty(run.Errors);
        Assert.Empty(run.Output);
    }

    [Fact]
    public async Task Remove_Force_skips_the_prompt_and_removes()
    {
        // Learn RD05: a Force parameter lets a non-interactive script or host run the command.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.delete", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxTestHost 10084 -Force");

        Assert.Empty(run.Errors);
        Assert.Equal("""["10084"]""", Assert.Single(server.ParamsOf("host.delete")).GetRawText());
    }

    [Fact]
    public async Task Remove_Force_with_an_explicit_Confirm_still_asks()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.delete", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxTestHost 10084 -Force -Confirm");

        Assert.NotEmpty(run.Errors);
        Assert.Empty(server.ParamsOf("host.delete"));
    }

    [Fact]
    public async Task Remove_Force_under_WhatIfPreference_removes_nothing()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("$WhatIfPreference = $true; Remove-ZbxTestHost 10084 -Force");

        Assert.Empty(run.Errors);
        Assert.Empty(server.ParamsOf("host.delete"));
    }

    [Fact]
    public async Task Remove_Confirm_false_skips_the_prompt_and_removes()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.delete", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxTestHost 10084 -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Equal("""["10084"]""", Assert.Single(server.ParamsOf("host.delete")).GetRawText());
    }

    [Fact]
    public async Task WhatIf_removes_nothing_even_with_Force()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxTestHost 10084 -Force -WhatIf");

        Assert.Empty(run.Errors);
        Assert.Empty(server.ParamsOf("host.delete"));
    }

    [Fact]
    public async Task Removing_resolves_ids_names_and_piped_objects_in_one_get()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10087", "web01")))
              .On("host.delete", """{"hostids":[]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        var piped = new ZabbixHost { HostId = 10086, HostName = "db01" };

        Run run = shell.Invoke(
            "@(10084, '10085', 'web01', 'ghost', $piped) | Remove-ZbxTestHost -Confirm:$false",
            new Dictionary<string, object?> { ["piped"] = piped });

        JsonElement get = Assert.Single(server.ParamsOf("host.get"));
        Assert.Equal("""["web01","ghost"]""", get.GetProperty("search").GetProperty("host").GetRawText());
        Assert.Equal("""["10084","10085","10087","10086"]""", Assert.Single(server.ParamsOf("host.delete")).GetRawText());
        ErrorRecord missing = Assert.Single(run.Errors);
        Assert.Equal(ErrorCategory.ObjectNotFound, missing.CategoryInfo.Category);
        Assert.Equal("ghost", missing.TargetObject);
    }

    [Fact]
    public void A_value_that_is_no_reference_fails_at_binding()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Remove-ZbxTestHost -InputObject (Get-Date) -Force");

        Assert.StartsWith("ParameterArgumentTransformationError,", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_resolves_whatever_its_case()
    {
        // Resolution compares names as a Get- -Name does, case-insensitively.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10087", "web01")))
              .On("host.delete", """{"hostids":["10087"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxTestHost WEB01 -Force");

        Assert.Empty(run.Errors);
        Assert.Equal("""["10087"]""", Assert.Single(server.ParamsOf("host.delete")).GetRawText());
    }

    [Fact]
    public async Task Two_objects_that_differ_only_in_case_make_a_name_ambiguous()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10087", "web01"), ("10088", "WEB01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxTestHost web01 -Force");

        Assert.StartsWith("ZabbixReferenceAmbiguous,", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("host.delete"));
    }

    [Fact]
    public async Task An_api_error_is_one_error_record_and_no_warning_saying_the_same()
    {
        // One failure is one error record: the SDK's "answered error" log line must not also surface as a WARNING
        // (as it would for New-ZbxHostGroup -Name <existing>).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.create", FakeZabbix.Error(-32602, "Invalid params.", "Host group \"Databases\" already exists."));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHostGroup Databases -Confirm:$false");

        Assert.Single(run.Errors);
        Assert.Empty(run.Warnings);
    }
}
