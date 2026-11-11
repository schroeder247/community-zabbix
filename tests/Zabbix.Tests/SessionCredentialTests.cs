// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The session credential against Zabbix's own rules: an ended session is renewed once, a refused login is not
/// hammered (the account block), a caller's cancellation never cancels the login others share, and the password never
/// prints.
/// </summary>
public sealed class SessionCredentialTests
{
    private const string Terminated = "\"error\":{\"code\":-32602,\"message\":\"Invalid params.\",\"data\":\"Session terminated, re-login, please.\"}";
    private const string Blocked = "\"error\":{\"code\":-32500,\"message\":\"Application error.\",\"data\":\"Incorrect user name or password or account is temporarily blocked.\"}";

    /// <summary>A server double: logins are answered by <see cref="Login"/>, every other call by <see cref="Call"/>, given its bearer.</summary>
    private sealed class SessionServer : HttpMessageHandler
    {
        /// <summary>
        /// A login's answer, as <c>user.login</c> with <c>userData: true</c> writes one (<c>CUser.php</c> 7.0:2342: the
        /// user record, its <c>sessionid</c> and <c>mfaid</c> among the fields); its argument numbers the logins.
        /// </summary>
        public Func<int, string> Login { get; init; } = index => User(index, mfaid: 0);

        public static string User(int index, int mfaid)
            => "\"result\":" + $$"""{"userid":"1","username":"Admin","roleid":"3","mfaid":{{mfaid}},"sessionid":"session-{{index}}","debug_mode":0}""";

        public Func<string?, string> Call { get; init; } = _ => "\"result\":[]";

        public TaskCompletionSource? LoginGate { get; init; }

        public TaskCompletionSource LoginArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> Methods { get; } = new();

        public ConcurrentQueue<JsonElement> LoginParameters { get; } = new();

        public ConcurrentQueue<string?> Bearers { get; } = new();

        public int Logins => Methods.Count(method => method == "user.login");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using JsonDocument envelope = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            string method = envelope.RootElement.GetProperty("method").GetString()!;
            string id = envelope.RootElement.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture);
            Methods.Enqueue(method);
            string member;
            if (method == "user.login")
            {
                Assert.Null(request.Headers.Authorization);
                LoginParameters.Enqueue(envelope.RootElement.GetProperty("params").Clone());
                int index = Logins;
                LoginArrived.TrySetResult();
                if (LoginGate is not null) await LoginGate.Task;
                member = Login(index);
            }
            else
            {
                string? bearer = request.Headers.Authorization?.Parameter;
                Bearers.Enqueue(bearer);
                member = Call(bearer);
                // CUser::logout answers true (CUser.php 8.0:2341-2380), unless the call is answered otherwise
                if (method == "user.logout" && member == "\"result\":[]") member = "\"result\":true";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"jsonrpc":"2.0",{{member}},"id":{{id}}}""", Encoding.UTF8, "application/json") };
        }
    }

    private static Harness Build(SessionServer server, ZabbixCredential credential, FakeTimeProvider? time = null) => TransportHarness.Build(server, token: null, configure: services =>
    {
        services.AddKeyedSingleton(Options.DefaultName, credential);
        if (time is not null) services.AddSingleton<TimeProvider>(time);
    });

    private static Task<JsonElement> HostGet(Harness harness, CancellationToken cancellationToken = default)
        => harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, cancellationToken);

    [Fact]
    public async Task A_user_who_must_complete_multi_factor_authentication_is_refused_once_with_the_reason()
    {
        // With MFA on for the user's group, user.login answers a session the server holds as "confirmation required"
        // (CUser.php 7.0:2331, 7.4:2337, 8.0:2447); the API refuses it as "Session terminated", which a naive credential
        // would renew on every request, minting an unconfirmed session each time. The API cannot complete MFA.
        using var server = new SessionServer { Login = index => SessionServer.User(index, mfaid: 1) };
        using Harness harness = Build(server, new ZabbixSessionCredential("mfa-person-7c", "zabbix"));

        var first = await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(() => HostGet(harness, TestContext.Current.CancellationToken));
        var second = await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(() => HostGet(harness, TestContext.Current.CancellationToken));

        Assert.Contains("multi-factor", first.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mfa-person-7c", first.Message, StringComparison.Ordinal);       // a user name is personal: never in a message
        Assert.Same(first, second.InnerException);                                              // answered from memory, with the cause
        Assert.Contains("API token", first.Message, StringComparison.Ordinal);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(1, server.Logins);                                                           // answered from memory the second time
        Assert.Empty(server.Bearers);                                                             // the unconfirmed session is never sent
    }

    private static ZabbixClient ServerOf(Harness harness) => harness.Provider.GetRequiredKeyedService<ZabbixClient>(Options.DefaultName);

    // A client the caller builds, and so disposes: one a container shares leaves its session to the container.
    private static ZabbixClient Owned(SessionServer server, ZabbixCredential credential, ILoggerFactory loggers) => ZabbixClient.CreateBuilder()
        .WithEndpoint("https://zbx.example/api_jsonrpc.php")
        .WithCredential(credential)
        .ConfigureHttpClientBuilder(http => http.ConfigurePrimaryHttpMessageHandler(() => server))
        .UseLogging(loggers)
        .Build();

    [Fact]
    public async Task Disposing_a_server_asynchronously_logs_its_session_out()
    {
        using var server = new SessionServer();
        var credential = new ZabbixSessionCredential("Admin", "zabbix");
        var logger = new FakeLogger();
        using var loggers = new SingleLoggerFactory(logger);
        ZabbixClient zabbix = Owned(server, credential, loggers);
        await zabbix.Hosts.GetAsync(TestContext.Current.CancellationToken);

        await zabbix.DisposeAsync();

        Assert.Equal(["user.login", "host.get", "user.logout"], server.Methods);
        Assert.Equal(["session-1", "session-1"], server.Bearers);                                 // the session it ends is its own
        Assert.Null(credential.Session);                                                          // the next request logs in again
        Assert.Contains(logger.Collector.GetSnapshot(), record => record.Id.Id == 4006 && record.Level == Microsoft.Extensions.Logging.LogLevel.Debug);
    }

    [Fact]
    public async Task Disposing_a_shared_server_leaves_its_session_and_the_container_ends_it()
    {
        using var server = new SessionServer();
        var credential = new ZabbixSessionCredential("Admin", "zabbix");
        using Harness harness = Build(server, credential);
        ZabbixClient zabbix = ServerOf(harness);
        await HostGet(harness, TestContext.Current.CancellationToken);

        await zabbix.DisposeAsync();
        await zabbix.Hosts.GetAsync(TestContext.Current.CancellationToken);                       // still running, on the same session

        Assert.Equal(["user.login", "host.get", "host.get"], server.Methods);
        await harness.Provider.DisposeAsync();
        Assert.Equal("user.logout", server.Methods.Last());
        Assert.Null(credential.Session);
        Assert.Throws<ObjectDisposedException>(() => zabbix.AllHosts);
    }

    [Fact]
    public async Task Disposing_the_container_asynchronously_logs_the_server_s_session_out()
    {
        using var server = new SessionServer();
        using Harness harness = Build(server, new ZabbixSessionCredential("Admin", "zabbix"));
        _ = ServerOf(harness);
        await HostGet(harness, TestContext.Current.CancellationToken);

        await harness.Provider.DisposeAsync();

        Assert.Equal("user.logout", server.Methods.Last());
    }

    [Fact]
    public async Task A_server_that_never_logged_in_sends_nothing_when_disposed()
    {
        using var server = new SessionServer();
        using Harness harness = Build(server, new ZabbixSessionCredential("Admin", "zabbix"));

        await ServerOf(harness).DisposeAsync();

        Assert.Empty(server.Methods);
    }

    [Fact]
    public async Task A_refused_logout_is_logged_and_the_disposal_completes()
    {
        using var server = new SessionServer { Call = _ => "\"error\":{\"code\":-32500,\"message\":\"Application error.\",\"data\":\"Not authorized.\"}" };
        var logger = new FakeLogger();
        using var loggers = new SingleLoggerFactory(logger);
        ZabbixClient zabbix = Owned(server, new ZabbixSessionCredential("Admin", "zabbix"), loggers);
        await Assert.ThrowsAsync<ZabbixApiException>(() => zabbix.Hosts.GetAsync(TestContext.Current.CancellationToken));

        await zabbix.DisposeAsync();                                                              // never throws

        Assert.Equal("user.logout", server.Methods.Last());
        Assert.Contains(logger.Collector.GetSnapshot(), record => record.Id.Id == 4007 && record.Exception is ZabbixApiException);
        Assert.Throws<ObjectDisposedException>(() => zabbix.AllHosts);
    }

    [Fact]
    public async Task Logging_out_ends_the_session_in_view_and_the_next_request_logs_in_again()
    {
        using var server = new SessionServer();
        var credential = new ZabbixSessionCredential("Admin", "zabbix");
        using Harness harness = Build(server, credential);
        await HostGet(harness, TestContext.Current.CancellationToken);

        await ServerOf(harness).LogOffAsync(TestContext.Current.CancellationToken);
        Assert.Null(credential.Session);
        await HostGet(harness, TestContext.Current.CancellationToken);

        Assert.Equal(["user.login", "host.get", "user.logout", "user.login", "host.get"], server.Methods);
        Assert.Equal(["session-1", "session-1", "session-2"], server.Bearers);
    }

    [Fact]
    public async Task Logging_out_a_server_that_holds_no_session_sends_nothing()
    {
        using var server = new SessionServer();
        using Harness harness = Build(server, new ZabbixSessionCredential("Admin", "zabbix"));

        await ServerOf(harness).LogOffAsync(TestContext.Current.CancellationToken);

        Assert.Empty(server.Methods);
    }

    [Fact]
    public async Task A_refused_logout_asked_for_in_code_throws_to_its_caller()
    {
        using var failing = new SessionServer { Call = _ => "\"error\":{\"code\":-32500,\"message\":\"Application error.\",\"data\":\"Not authorized.\"}" };
        var credential = new ZabbixSessionCredential("Admin", "zabbix");
        using Harness refused = Build(failing, credential);
        await Assert.ThrowsAsync<ZabbixApiException>(() => HostGet(refused, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ZabbixApiException>(() => ServerOf(refused).LogOffAsync(TestContext.Current.CancellationToken));
        Assert.Equal("user.logout", failing.Methods.Last());
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed", Justification = "The first call is awaited in the finally block, before the harness is disposed.")]
    public async Task A_password_provider_that_blocks_does_not_hold_a_second_caller_past_its_own_cancellation()
    {
        // The provider is unknown code (a synchronous vault read): run under the credential's lock, it would hold every
        // other caller on the lock, where no caller's token is observed.
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var credential = new ZabbixSessionCredential("Admin", _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None);
            return ValueTask.FromResult("zabbix");
        });
        using var server = new SessionServer();
        using Harness harness = Build(server, credential);
        // Run on the caller's thread, the provider would hold this call until it gave up, and the session would be there
        // for the impatient caller below.
        Task first = HostGet(harness, TestContext.Current.CancellationToken);
        bool cancelledInTime;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            using var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            Exception? second = await Record.ExceptionAsync(() => HostGet(harness, impatient.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            cancelledInTime = second is OperationCanceledException;
        }
        finally
        {
            release.Set();
            await first;
        }

        Assert.True(cancelledInTime);
        Assert.Equal(1, server.Logins);
    }

    [Fact]
    public async Task Disposing_a_server_synchronously_leaves_its_session_to_expire()
    {
        using var server = new SessionServer();
        var credential = new ZabbixSessionCredential("Admin", "zabbix");
        using Harness harness = Build(server, credential);
        await HostGet(harness, TestContext.Current.CancellationToken);

#pragma warning disable CA1849 // the synchronous Dispose is what this test is about
        ServerOf(harness).Dispose();
#pragma warning restore CA1849

        Assert.Equal(["user.login", "host.get"], server.Methods);
        Assert.Equal("session-1", credential.Session);
    }

    [Fact]
    public async Task The_login_asks_for_the_user_record_so_it_can_see_multi_factor_authentication()
    {
        using var server = new SessionServer();
        using Harness harness = Build(server, new ZabbixSessionCredential("Admin", "zabbix"));

        await HostGet(harness, TestContext.Current.CancellationToken);

        JsonElement login = Assert.Single(server.LoginParameters);
        Assert.True(login.GetProperty("userData").GetBoolean());
        Assert.Equal("Admin", login.GetProperty("username").GetString());
        Assert.Equal(["session-1"], server.Bearers);                                              // the record's sessionid is the token
    }

    [Fact]
    public async Task An_ended_session_is_renewed_once_and_the_call_is_sent_again()
    {
        using var server = new SessionServer { Call = bearer => bearer == "session-1" ? Terminated : "\"result\":[]" };
        using Harness harness = Build(server, new ZabbixSessionCredential("Admin", "zabbix"));

        await HostGet(harness, TestContext.Current.CancellationToken);

        Assert.Equal(["user.login", "host.get", "user.login", "host.get"], server.Methods);
        Assert.Equal(["session-1", "session-2"], server.Bearers);
        Assert.Contains(harness.Logs.GetSnapshot(), record => record.Id.Id == 4004);
    }

    [Fact]
    public async Task Two_callers_refused_on_one_ended_session_renew_it_with_one_login()
    {
        using var bothRefused = new Barrier(2);
        using var server = new SessionServer
        {
            Call = bearer =>
            {
                if (bearer != "session-1") return "\"result\":[]";
                bothRefused.SignalAndWait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);   // both in flight on the old session
                return Terminated;
            },
        };
        using Harness harness = Build(server, new ZabbixSessionCredential("Admin", "zabbix"));

        await Task.WhenAll(
            Task.Run(() => HostGet(harness, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken),
            Task.Run(() => HostGet(harness, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));

        Assert.Equal(2, server.Logins);                                                           // the first, and one renewal for both
        Assert.Equal(2, server.Bearers.Count(bearer => bearer == "session-2"));
    }

    [Fact]
    public async Task A_session_refused_twice_fails_the_call()
    {
        using var server = new SessionServer { Call = _ => Terminated };
        using Harness harness = Build(server, new ZabbixSessionCredential("Admin", "zabbix"));

        ZabbixApiException failure = await Assert.ThrowsAsync<ZabbixApiException>(async () => await HostGet(harness, TestContext.Current.CancellationToken));

        Assert.Equal(ZabbixApiErrorKind.SessionTerminated, failure.Kind);
        Assert.Equal(2, server.Logins);
        Assert.Equal(2, server.Bearers.Count);
    }

    [Fact]
    public async Task An_api_token_the_server_no_longer_knows_is_not_renewed()
    {
        using var server = new SessionServer { Call = _ => Terminated };
        using Harness harness = Build(server, new ZabbixApiTokenCredential("tok"));

        await Assert.ThrowsAsync<ZabbixApiException>(async () => await HostGet(harness, TestContext.Current.CancellationToken));

        Assert.Equal(["host.get"], server.Methods);
    }

    [Fact]
    public async Task A_refused_login_is_not_sent_again_inside_the_backoff_window()
    {
        var time = new FakeTimeProvider();
        using var server = new SessionServer { Login = _ => Blocked };
        using Harness harness = Build(server, new ZabbixSessionCredential("Admin", "wrong"), time);

        ZabbixApiException first = await Assert.ThrowsAsync<ZabbixApiException>(async () => await HostGet(harness, TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromSeconds(29));
        ZabbixApiException second = await Assert.ThrowsAsync<ZabbixApiException>(async () => await HostGet(harness, TestContext.Current.CancellationToken));

        Assert.Equal(ZabbixApiErrorKind.LoginFailed, first.Kind);
        Assert.Equal(ZabbixApiErrorKind.LoginFailed, second.Kind);
        Assert.Same(first, second.InnerException);                                              // the server's own refusal stays the cause
        Assert.Equal(1, server.Logins);

        time.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<ZabbixApiException>(async () => await HostGet(harness, TestContext.Current.CancellationToken));

        Assert.Equal(2, server.Logins);
        Assert.Empty(server.Bearers);
    }

    [Fact]
    public async Task A_caller_that_cancels_does_not_cancel_the_login_the_others_share()
    {
        using var server = new SessionServer { LoginGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using Harness harness = Build(server, new ZabbixSessionCredential("Admin", "zabbix"));
        using var impatient = new CancellationTokenSource();

#pragma warning disable CA2025 // both calls are awaited below, before the harness and the token source are disposed
        Task first = HostGet(harness, impatient.Token);
        await server.LoginArrived.Task;
        Task second = HostGet(harness, TestContext.Current.CancellationToken);
#pragma warning restore CA2025
        await impatient.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        server.LoginGate.SetResult();
        await second;

        Assert.Equal(1, server.Logins);
        Assert.Equal(["session-1"], server.Bearers);
    }

    [Fact]
    public async Task A_password_provider_is_asked_for_each_login_and_not_before()
    {
        int asked = 0;
        using var server = new SessionServer { Call = bearer => bearer == "session-1" ? Terminated : "\"result\":[]" };
        var credential = new ZabbixSessionCredential("Admin", _ => { asked++; return new ValueTask<string>("zabbix"); });
        using Harness harness = Build(server, credential);
        Assert.Equal(0, asked);

        await HostGet(harness, TestContext.Current.CancellationToken);

        Assert.Equal(2, asked);
    }

    [Fact]
    public void The_login_parameters_and_the_credential_never_print_the_password()
    {
        const string Password = "hunter2-7f3a";

        Assert.DoesNotContain(Password, new ZabbixLoginParameters("Admin", Password).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, new ZabbixSessionCredential("Admin", Password).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("tok-secret", new ZabbixApiTokenCredential("tok-secret").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("person-41", new ZabbixSessionCredential("person-41", Password).ToString(), StringComparison.Ordinal);
        Assert.NotNull(typeof(ZabbixSessionCredential).GetProperty(nameof(ZabbixSessionCredential.UserName))!.GetCustomAttributes(typeof(ZabbixPersonalAttribute), false).SingleOrDefault());
    }
}
