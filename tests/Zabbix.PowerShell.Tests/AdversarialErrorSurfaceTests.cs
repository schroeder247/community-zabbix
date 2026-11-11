// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Reflection;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// Error surfaces. Every error record a cmdlet writes carries a real category, a target and a
/// specific id; no secret given to <c>Connect-ZbxServer</c> reaches any stream, <c>$Error</c>, or the text of the
/// connection/context objects. Plus the PowerShell idioms a reviewer checks by eye: <c>ShouldProcess</c> on every change,
/// <c>-WhatIf</c> that sends nothing.
/// </summary>
public sealed class AdversarialErrorSurfaceTests
{
    private const string Secret = "zbx-test-adversarial-SECRET-7f3c";

    // What the module itself must never use as an error id: the CLR type name of a BCL exception says nothing a user can
    // switch on (ZabbixErrors.IdOf falls back to ZabbixErrorType.Of, whose last resort is the full type name).
    private static bool IsGeneric(string errorId) => errorId.StartsWith("System.", StringComparison.Ordinal);

    /// <summary>A failing command, and what the fake server does: answers an API error, answers nothing found, or is down.</summary>
    public static TheoryData<string, string> Failures => new()
    {
        { "Get-ZbxHost", "api-error" },
        { "Get-ZbxHost -Name web-01", "api-error" },
        { "Get-ZbxHost -CountOutput", "api-error" },
        { "Get-ZbxItem -Host web-01", "api-error" },
        { "New-ZbxHostGroup -Name g", "api-error" },
        { "Set-ZbxHostGroup -InputObject 5 -Name h", "api-error" },
        { "Remove-ZbxHost 10084 -Confirm:$false", "api-error" },
        { "Remove-ZbxHost ghost -Confirm:$false", "empty" },
        { "Disable-ZbxHost 10084 -Force -Confirm:$false", "api-error" },
        { "Invoke-ZbxApi -Method host.get -Body @{}", "api-error" },
        { "Get-ZbxHost", "down" },
        { "New-ZbxHostGroup -Name g", "down" },
        { "Remove-ZbxHost 10084 -Confirm:$false", "down" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Every_api_or_connection_error_record_has_a_real_category_and_a_specific_id(string script, string answer)
    {
        Run run = await FailAsync(script, answer);

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.NotEqual(ErrorCategory.NotSpecified, error.CategoryInfo.Category);
        Assert.False(IsGeneric(error.FullyQualifiedErrorId), error.FullyQualifiedErrorId);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Every_error_record_names_the_object_the_command_acted_on(string script, string answer)
    {
        // Every error record carries a TargetObject, not only Remove-Zbx* (the confirmed ids) and a name that resolves to
        // nothing: a Get-/New-/Set-/Disable-/Invoke-ZbxApi error names what it was working on too.
        Run run = await FailAsync(script, answer);

        Assert.NotNull(Assert.Single(run.Errors).TargetObject);
    }

    [Theory]
    [InlineData("Get-ZbxQueue -Server 127.0.0.1:1")]
    [InlineData("Send-ZbxData web-01 agent.ping 1 -Server 127.0.0.1:1 -Confirm:$false")]
    [InlineData("Test-ZbxItem -Data @{ key = 'k' } -Server 127.0.0.1:1")]
    public async Task A_trapper_that_refuses_the_connection_is_a_ConnectionError(string script)
    {
        // ZabbixErrors.CategoryOf maps the SocketException (or IOException over one) the server's own protocol throws to
        // ConnectionError, as it does HttpRequestException; otherwise every trapper/agent/proxy wire failure would be
        // NotSpecified ("ZabbixWire.ConnectionRefused,…GetZbxQueueCommand [NotSpecified]").
        Run run = await FailAsync(script, "empty");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.Equal(ErrorCategory.ConnectionError, error.CategoryInfo.Category);
    }

    [Theory]
    [InlineData("New-ZbxItem -HostId 1 -Name n -Key k -Confirm:$false")]
    [InlineData("New-ZbxHostGroup -Confirm:$false")]
    [InlineData("New-ZbxHost -Group 1 -Confirm:$false")]
    public async Task A_missing_required_field_is_an_error_with_a_named_id_not_a_CLR_type_name(string script)
    {
        // The generated required-field gate's error has an id of its own: a bare ArgumentException would fall back to the
        // type name ("System.ArgumentException,Community.Zabbix.PowerShell.Commands.Generated.NewItemCommand"), and a script could
        // not tell "you left out -Type" from any other bad argument.
        Run run = await FailAsync(script, "empty");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.False(IsGeneric(error.FullyQualifiedErrorId), error.FullyQualifiedErrorId);
        Assert.NotNull(error.TargetObject);
    }

    private static async Task<Run> FailAsync(string script, string answer)
    {
        FakeZabbix server = await FakeZabbix.StartAsync();
        try
        {
            using Shell shell = Shell.Open();
            shell.Connect(server);
            Script(server, answer);
            if (answer == "down") await server.DisposeAsync();
            return shell.Invoke(script);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("-ApiToken (ConvertTo-SecureString $secret -AsPlainText -Force)")]
    [InlineData("-Credential ([pscredential]::new('Admin', (ConvertTo-SecureString $secret -AsPlainText -Force)))")]
    public async Task The_secret_given_to_Connect_ZbxServer_appears_in_no_stream_no_error_and_no_object_text(string credential)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("user.login", FakeZabbix.Error(-32602, "Invalid params.", "Incorrect user name or password or account is temporarily blocked."));
        server.On("host.get", FakeZabbix.Error(-32602, "Invalid params.", "Not authorized."));
        server.On("hostgroup.get", "{\"broken\":");
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($$"""
            $VerbosePreference = 'Continue'; $DebugPreference = 'Continue'; $InformationPreference = 'Continue'
            Connect-ZbxServer -Uri '{{server.Endpoint}}' {{credential}} -Name prod -PassThru
            Get-ZbxHost
            Get-ZbxHostGroup
            Get-ZbxApiInfo
            Invoke-ZbxApi -Method host.get -Body @{}
            $context = Get-ZbxContext
            $context | Format-List * -Force | Out-String -Width 4096
            $context.Current | Format-List * -Force | Out-String -Width 4096
            $context.Current | Select-Object * | Out-String -Width 4096
            $context.Current.ToString()
            $context.Current.Server | Format-List * -Force | Out-String -Width 4096
            $context.Current | ConvertTo-Json -Depth 3 -WarningAction SilentlyContinue
            $context.Current | Export-Clixml -Path $clixml -Depth 3; Get-Content -Raw $clixml
            $Error | Out-String -Width 4096
            $Error | Format-List * -Force | Out-String -Width 4096
            $Error | ForEach-Object { $_.Exception | Format-List * -Force | Out-String -Width 4096 }
            """, new Dictionary<string, object?> { ["secret"] = Secret, ["clixml"] = Path.Combine(Path.GetTempPath(), $"advps-{Guid.NewGuid():N}.xml") });

        IEnumerable<string> everything = run.Output.Select(o => o?.ToString() ?? string.Empty)
            .Concat(run.Errors.SelectMany(e => new[] { e.ToString(), e.Exception.ToString(), e.ErrorDetails?.Message ?? string.Empty, e.TargetObject?.ToString() ?? string.Empty }))
            .Concat(run.Warnings).Concat(run.Debug).Concat(run.Verbose).Concat(run.Information.Select(i => i.MessageData?.ToString() ?? string.Empty));
        Assert.NotEmpty(run.Errors);
        Assert.All(everything, text => Assert.DoesNotContain(Secret, text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_secret_in_the_URL_user_info_appears_in_no_object_text()
    {
        // A URL-borne secret is refused or stripped like the token/password parameters (the test above). Kept as given,
        // the password would appear in ZabbixConnection.ToString() ("127.0.0.1:41219 (http://admin:<secret>@…)"), its
        // Endpoint in Format-List, Get-ZbxContext's Servers, Get-ZbxApiInfo's Endpoint column, and the SDK's own
        // Debug/Verbose lines ("Zabbix server … at http://admin:<secret>@…", "Connected … to http://admin:<secret>@…").
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        var withUserInfo = new UriBuilder(server.Endpoint) { UserName = "admin", Password = Secret }.Uri;

        Run run = shell.Invoke($"""
            $VerbosePreference = 'Continue'; $DebugPreference = 'Continue'
            Connect-ZbxServer -Uri '{withUserInfo}' -PassThru | Out-String -Width 4096
            (Get-ZbxContext).Current.ToString()
            (Get-ZbxContext) | Format-List * -Force | Out-String -Width 4096
            Get-ZbxApiInfo | Out-String -Width 4096
            """);

        Assert.All(run.Output.Select(o => o.ToString()).Concat(run.Errors.Select(e => e.ToString())).Concat(run.Verbose).Concat(run.Debug)
            .Concat(run.Information.Select(i => i.MessageData?.ToString() ?? string.Empty)), text => Assert.DoesNotContain(Secret, text, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_cmdlet_that_changes_a_server_supports_ShouldProcess()
    {
        // Send-ZbxProxyData uploads history into the server as a proxy (proxy data), the same kind of change
        // Send-ZbxData/Send-ZbxHistory confirm, so it takes -WhatIf/-Confirm too.
        // The change verbs, and the hand-written commands whose verb does not say it (Send- pushes data into the server).
        string[] changes = [VerbsCommon.New, VerbsCommon.Set, VerbsCommon.Remove, VerbsCommon.Clear, VerbsCommon.Add, VerbsCommon.Rename,
            VerbsCommon.Move, VerbsCommon.Copy, VerbsCommon.Reset, VerbsData.Import, VerbsData.Publish, VerbsData.Update, VerbsData.Sync,
            VerbsLifecycle.Disable, VerbsLifecycle.Enable, VerbsLifecycle.Start, VerbsLifecycle.Stop, VerbsLifecycle.Restart, VerbsLifecycle.Confirm,
            VerbsCommunications.Send];
        // Builders: a New- that returns a local record and talks to no server (they derive ZabbixCmdlet, not ZabbixServerCmdlet).
        string[] missing = [.. ModuleUnderTest.All.SelectMany(module => module.Assembly.GetTypes())
            .Select(type => (Type: type, Cmdlet: type.GetCustomAttribute<CmdletAttribute>()))
            .Where(c => c.Cmdlet is not null && changes.Contains(c.Cmdlet.VerbName) && !c.Cmdlet.SupportsShouldProcess)
            .Where(c => typeof(ZabbixServerCmdlet).IsAssignableFrom(c.Type) || c.Cmdlet!.VerbName == VerbsCommunications.Send)
            .Select(c => $"{c.Cmdlet!.VerbName}-{c.Cmdlet.NounName}")
            .Order(StringComparer.Ordinal)];

        Assert.True(missing.Length == 0, "no ShouldProcess: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("Clear-ZbxHistory 10084 -WhatIf -Force")]
    [InlineData("Disable-ZbxHost 10084 -WhatIf -Force")]
    [InlineData("Enable-ZbxHost web-01 -WhatIf -Force")]
    [InlineData("Send-ZbxHistory -HostName h -Key k -Value 1 -Clock 1 -WhatIf")]
    [InlineData("Initialize-ZbxAgentHost -Key k -HostName h -WhatIf")]
    [InlineData("Set-ZbxHost web-01 -Description d -WhatIf")]
    [InlineData("Remove-ZbxHost web-01 -WhatIf")]
    [InlineData("New-ZbxItem -HostId 1 -Name n -Key k -Type Zabbix -ValueType Float -WhatIf")]
    public async Task WhatIf_changes_nothing_on_the_server(string script)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01"))).On("item.get", "[]").On("hostgroup.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(script);

        Assert.Empty(run.Errors);
        Assert.All(server.Calls, call => Assert.True(call.Method.EndsWith(".get", StringComparison.Ordinal) || call.Method == "apiinfo.version", call.Method));
    }

    private static void Script(FakeZabbix server, string answer)
    {
        switch (answer)
        {
            case "api-error":
                foreach (string method in new[] { "host.get", "item.get", "hostgroup.get", "hostgroup.create", "hostgroup.update", "host.delete", "host.update" })
                    server.On(method, FakeZabbix.Error(-32602, "Invalid params.", "Incorrect value for field \\\"name\\\"."));
                break;
            case "empty":
                foreach (string method in new[] { "host.get", "item.get", "hanode.get" }) server.On(method, "[]");
                break;
        }
    }
}
