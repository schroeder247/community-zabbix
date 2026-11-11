// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The per-runspace context and the connection's lifetime.</summary>
public sealed class ContextTests
{
    [Fact]
    public async Task Two_runspaces_have_their_own_contexts()
    {
        await using FakeZabbix first = await FakeZabbix.StartAsync();
        await using FakeZabbix second = await FakeZabbix.StartAsync();
        using Shell a = Shell.Open();
        using Shell b = Shell.Open();

        a.Connect(first);
        b.Connect(second);

        ZabbixContext contextA = (ZabbixContext)a.Invoke("Get-ZbxContext").Output.Single().BaseObject;
        ZabbixContext contextB = (ZabbixContext)b.Invoke("Get-ZbxContext").Output.Single().BaseObject;
        Assert.NotSame(contextA, contextB);
        Assert.Equal(first.Endpoint, contextA.Current!.Endpoint);
        Assert.Equal(second.Endpoint, contextB.Current!.Endpoint);
        Assert.Single(contextA.Servers);
        Assert.Single(contextB.Servers);
    }

    [Fact]
    public async Task Get_ZbxContext_shows_how_many_sessions_are_running()
    {
        // The view reads the context's own sessions, not an internal property that would show every context one session: @($null).Count.
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("active checks", """{"response":"success","config_revision":1}""").On("agent data", FakeTrapper.Success(0, 0));
        using Shell shell = Shell.Open();
        try
        {
            string none = Assert.Single(shell.Invoke("Get-ZbxContext | Out-String").Output).ToString();
            shell.Invoke($"Start-ZbxActiveAgent one web-01 '127.0.0.1:{trapper.Port}' -Confirm:$false; Start-ZbxActiveAgent two web-02 '127.0.0.1:{trapper.Port}' -Confirm:$false");
            string two = Assert.Single(shell.Invoke("Get-ZbxContext | Out-String").Output).ToString();

            Assert.Matches(@"(?m)^Sessions\s*:\s*0\s*$", none);
            Assert.Matches(@"(?m)^Sessions\s*:\s*2\s*$", two);
            Assert.Equal(2, ((ZabbixContext)Assert.Single(shell.Invoke("Get-ZbxContext").Output).BaseObject).SessionCount);
        }
        finally
        {
            shell.Invoke("'one', 'two' | Stop-ZbxActiveAgent -Confirm:$false");
        }
    }

    [Fact]
    public async Task A_runspace_that_never_connected_is_not_connected()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell connected = Shell.Open();
        using Shell other = Shell.Open();
        connected.Connect(server);

        Run run = other.Invoke("Get-ZbxApiVersion");

        Assert.Empty(run.Output);
        Assert.StartsWith("ZabbixNotConnected,", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("New-ZbxHostGroup -Name 'Databases' -Force")]
    [InlineData("Set-ZbxHostGroup -InputObject 'Databases' -Name 'SQL servers' -Force")]
    [InlineData("Remove-ZbxHostGroup -InputObject 'Databases' -Force")]
    public void A_generated_write_cmdlet_with_no_connection_fails_with_ZabbixNotConnected_not_a_null_reference(string script)
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke(script);

        Assert.Empty(run.Output);
        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("ZabbixNotConnected,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    [Fact]
    public void A_generated_write_cmdlet_with_no_connection_never_prompts_before_failing()
    {
        // -Force is deliberately omitted: the connection is checked in ZabbixServerCmdlet.BeginProcessing, before any
        // subclass's ProcessRecord/ShouldChange runs. A headless host (no console attached, like this test's default
        // runspace) therefore gets the connection error, not a bare NullReferenceException out of PowerShell's own
        // ConsoleHostUserInterface.PromptForChoice, which a ShouldChange prompt via ShouldContinue would reach first.
        // This holds for every derived cmdlet, not just New-ZbxHostGroup.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("New-ZbxHostGroup -Name 'Databases'");

        Assert.Empty(run.Output);
        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("ZabbixNotConnected,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Named_servers_live_side_by_side_and_the_first_becomes_current()
    {
        await using FakeZabbix prod = await FakeZabbix.StartAsync();
        await using FakeZabbix test = await FakeZabbix.StartAsync();
        test.On("apiinfo.version", "\"8.0.0\"");
        using Shell shell = Shell.Open();

        shell.Connect(prod, "-Name prod");
        shell.Connect(test, "-Name test -NoDefault");
        Run versions = shell.Invoke("Get-ZbxApiVersion; Get-ZbxApiVersion -Connection test");

        Assert.Equal(["prod", "test"], shell.Context.Servers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("prod", shell.Context.Current!.Name);
        Assert.Equal([new Version(7, 0, 31), new Version(8, 0, 0)], versions.Output.Select(o => (Version)o.BaseObject));
    }

    [Fact]
    public async Task Reconnecting_under_a_name_closes_the_old_connection()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server, "-Name prod");
        ZabbixConnection old = shell.Context.Current!;

        shell.Connect(server, "-Name prod");

        Assert.NotSame(old, shell.Context.Current);
        Assert.True(old.IsClosed);
        Assert.True(old.IsReleased);
    }

    [Fact]
    public async Task Disconnecting_does_not_dispose_a_connection_under_another_runspaces_call()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "Zabbix server"))).Stall("host.get");
        using Shell owner = Shell.Open();
        using Shell borrower = Shell.Open();
        owner.Connect(server);
        ZabbixConnection connection = owner.Context.Current!;
        TaskCompletionSource<string> arrived = server.ExpectArrival();

        await using Call call = borrower.Start("Get-ZbxHost -Connection $connection", new Dictionary<string, object?> { ["connection"] = connection });
        Assert.Equal("host.get", await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        owner.Invoke("Disconnect-ZbxServer");
        Assert.True(connection.IsClosed);
        // This wait can fail on a loaded runner, so on failure the message says what the borrower's call was doing. Built only then, and never by enumerating an open PSDataCollection (that blocks until the pipeline ends).
        if (connection.IsReleased)
            Assert.Fail($"released under the borrower's call, which is {call.InvocationStateInfo.State} (task {call.Running.Status}, {call.Streams.Error.Count} error(s), {call.Running.Exception?.GetBaseException().Message})");

        server.Release("host.get");
        PSDataCollection<PSObject> hosts = await call.Running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal("Zabbix server", ((ZabbixHost)Assert.Single(hosts).BaseObject).TechnicalName);
        Assert.Empty(call.Streams.Error);
        Assert.True(connection.IsReleased);
    }

    [Fact]
    public async Task A_closed_connection_refuses_new_calls_with_an_error()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);
        ZabbixConnection connection = shell.Context.Current!;
        shell.Invoke("Disconnect-ZbxServer");

        Run run = shell.Invoke("Get-ZbxApiVersion -Connection $connection", new Dictionary<string, object?> { ["connection"] = connection });

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.Equal(ErrorCategory.ResourceUnavailable, error.CategoryInfo.Category);
    }

    [Fact]
    public async Task Removing_the_module_closes_the_runspaces_connections()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);
        ZabbixConnection connection = shell.Context.Current!;

        shell.Invoke("Remove-Module Community.Zabbix, Community.Zabbix.Agent, Community.Zabbix.Proxy");

        Assert.True(connection.IsReleased);
    }

    [Fact]
    public async Task A_credential_logs_in_on_first_use_and_the_session_is_the_token()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("user.login", """{"userid":"1","mfaid":0,"sessionid":"session-1"}""").On("host.get", "[]");   // userData: true → the user record, as 7.0/8.0 answer
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"""
            $credential = [pscredential]::new('Admin', (ConvertTo-SecureString 'zabbix' -AsPlainText -Force))
            Connect-ZbxServer -Uri '{server.Endpoint}' -Credential $credential
            Get-ZbxHost
            Get-ZbxHost
            """);

        Assert.Empty(run.Errors);
        System.Text.Json.JsonElement login = Assert.Single(server.ParamsOf("user.login"));
        Assert.Equal("Admin", login.GetProperty("username").GetString());
        Assert.Equal("zabbix", login.GetProperty("password").GetString());
        Assert.All(server.Calls.Where(call => call.Method == "host.get"), call => Assert.Equal("Bearer session-1", call.Authorization));
    }

    [Fact]
    public async Task Disconnect_ZbxServer_logs_a_login_session_out_before_it_returns()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        api.On("user.login", """{"userid":"1","username":"Admin","mfaid":0,"sessionid":"0123456789abcdef0123456789abcdef"}""");
        api.On("user.logout", "true");
        api.On("host.get", "[]");
        using Shell shell = Shell.Open();
        shell.Invoke($"Connect-ZbxServer -Uri '{api.Endpoint}' -Credential ([pscredential]::new('Admin', (ConvertTo-SecureString 'zabbix' -AsPlainText -Force)))");
        Assert.Empty(shell.Invoke("Get-ZbxHost").Errors);

        Run disconnected = shell.Invoke("Disconnect-ZbxServer");

        Assert.Empty(disconnected.Errors);
        Assert.Single(api.ParamsOf("user.logout"));
    }

    [Fact]
    public async Task Disconnect_ZbxServer_sends_no_logout_for_an_API_token()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        api.On("host.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(api);
        shell.Invoke("Get-ZbxHost");

        Run disconnected = shell.Invoke("Disconnect-ZbxServer");

        Assert.Empty(disconnected.Errors);
        Assert.Empty(api.ParamsOf("user.logout"));
    }

    [Fact]
    public async Task SkipCertificateCheck_accepts_a_self_signed_frontend_and_warns()
    {
        // Invoke-RestMethod's knob: without it a self-signed frontend cannot be reached at all.
        using System.Security.Cryptography.X509Certificates.X509Certificate2 certificate = SelfSigned();
        await using FakeZabbix server = await FakeZabbix.StartAsync(certificate);
        using Shell shell = Shell.Open();

        Run refused = shell.Invoke($"Connect-ZbxServer -Uri '{server.Endpoint}' -Name checked");
        Run accepted = shell.Invoke($"Connect-ZbxServer -Uri '{server.Endpoint}' -Name skipped -SkipCertificateCheck; Get-ZbxApiVersion -Connection skipped");

        Assert.NotEmpty(refused.Errors);
        Assert.Empty(accepted.Errors);
        Assert.Contains(accepted.Warnings, warning => warning.Contains("not checked", StringComparison.Ordinal));
        Assert.Equal("7.0.31", Assert.Single(accepted.Output).ToString());
    }

    [Fact]
    public void A_token_for_plain_http_on_another_host_is_refused_unless_AllowUnencryptedCredential_is_given()
    {
        // zabbix.invalid never resolves (RFC 2606), so the allowed connection fails on the network, not on the refusal.
        using Shell shell = Shell.Open();
        const string Connect = "Connect-ZbxServer -Uri 'http://zabbix.invalid/api_jsonrpc.php' -ApiToken (ConvertTo-SecureString 'token' -AsPlainText -Force)";

        Run refused = shell.Invoke($"{Connect} -Name clear");
        Run allowed = shell.Invoke($"{Connect} -Name allowed -AllowUnencryptedCredential");

        Assert.Contains(refused.Errors, error => error.ToString().Contains("AllowUnencryptedCredentials", StringComparison.Ordinal));
        Assert.DoesNotContain(allowed.Errors, error => error.ToString().Contains("AllowUnencryptedCredentials", StringComparison.Ordinal));
        Assert.Contains(allowed.Warnings, warning => warning.Contains("in clear", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TimeoutSec_is_the_requests_deadline()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.Stall("host.get");
        using Shell shell = Shell.Open();
        shell.Invoke($"Connect-ZbxServer -Uri '{server.Endpoint}' -TimeoutSec 1");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Run run = shell.Invoke("Get-ZbxHost");

        Assert.Single(run.Errors);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Proxy_carries_the_requests()
    {
        // The fake answers a proxied request (an absolute-form target) as its own, so a server name nothing resolves is
        // reached only through the proxy.
        await using FakeZabbix proxy = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Connect-ZbxServer -Uri 'http://zabbix.invalid/api_jsonrpc.php' -Proxy '{proxy.BaseAddress}' -PassThru | ForEach-Object ApiVersion");

        Assert.Empty(run.Errors);
        Assert.Equal("7.0.31", Assert.Single(run.Output).ToString());
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 SelfSigned()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=127.0.0.1", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        var names = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using System.Security.Cryptography.X509Certificates.X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(created.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12), null);
    }
}
