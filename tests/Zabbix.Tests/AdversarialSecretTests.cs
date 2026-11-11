// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Tests.Conformance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>
/// A known secret through every text the SDK produces: sensitive data never reaches a sink. One token string goes in as an API token, a password, a session token and
/// a PSK; the test drives each boundary (a call that succeeds, one the server refuses, one the HTTP layer fails, a login
/// that is refused and one that is renewed) with every log level on, every activity source sampled, and then searches
/// every log record (message, structured state, exception), every span (tags, events, status) and every exception's
/// <c>ToString()</c> for it. The grep half is <see cref="AdversarialDesignConformanceTests.Every_secret_named_member_carries_a_data_classification"/>.
/// </summary>
[Collection(AdversarialSecretIsolation.Name)]
public sealed class AdversarialSecretTests
{
    private const string Secret = "zbx-test-adversarial-SECRET-7f3c";

    [Fact]
    public async Task An_api_token_never_reaches_a_log_a_span_or_an_exception()
    {
        using var server = new SecretServer { Reply = (_, id) => Ok("[]", id) };
        using var capture = new SpanCapture();
        using Harness harness = TransportHarness.Build(server, token: Secret);

        await SendAsync(harness, "host.get");
        server.Reply = (_, id) => Error(-32602, "Invalid params.", "Not authorised.", id);
        Exception refused = await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(harness, "host.get"));
        server.Reply = (_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("forbidden", Encoding.UTF8, "text/plain") };
        Exception failed = await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(harness, "host.get"));
        server.Reply = (_, id) => Json("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":" + (id + 1).ToString(CultureInfo.InvariantCulture) + "}");
        Exception mismatched = await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(harness, "host.get"));

        AssertNoSecret(harness.Logs, capture, [refused, failed, mismatched], Secret);
    }

    [Fact]
    public async Task A_refused_password_never_reaches_a_log_a_span_or_an_exception()
    {
        using var server = new SecretServer
        {
            Reply = (_, id) => Error(-32500, "Application error.", "Incorrect user name or password or account is temporarily blocked.", id),
        };
        using var capture = new SpanCapture();
        using Harness harness = TransportHarness.Build(server, token: null, configure: services =>
            services.AddKeyedSingleton<ZabbixCredential>(Options.DefaultName, new ZabbixSessionCredential("Admin", Secret)));

        Exception refused = await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(harness, "host.get"));

        AssertNoSecret(harness.Logs, capture, [refused], Secret);
    }

    [Fact]
    public async Task A_session_token_and_its_renewal_never_reach_a_log_a_span_or_an_exception()
    {
        int calls = 0;
        using var server = new SecretServer
        {
            Reply = (method, id) => method == "user.login"
                ? Ok("""{"userid":"1","mfaid":0,"sessionid":""" + JsonSerializer.Serialize("session-" + Secret) + "}", id)          // user.login with userData, as every 7.0-8.0 server answers it
                : Interlocked.Increment(ref calls) <= 2
                    ? Error(-32602, "Invalid params.", "Session terminated, re-login, please.", id)
                    : Ok("[]", id),
        };
        using var capture = new SpanCapture();
        using Harness harness = TransportHarness.Build(server, token: null, configure: services =>
            services.AddKeyedSingleton<ZabbixCredential>(Options.DefaultName, new ZabbixSessionCredential("Admin", Secret)));

        Exception renewedAndRefused = await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(harness, "host.get"));
        await SendAsync(harness, "host.get");

        AssertNoSecret(harness.Logs, capture, [renewedAndRefused], Secret);
    }

    [Fact]
    public void A_credential_s_text_form_never_carries_its_secret()
    {
        string[] texts = [new ZabbixApiTokenCredential(Secret).ToString()!, new ZabbixSessionCredential("Admin", Secret).ToString()!];

        Assert.DoesNotContain(texts, text => text.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public void A_pre_shared_key_that_fails_validation_is_not_echoed_in_the_failure()
    {
        var options = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = "", Psk = Secret, CrlFile = "x" };

        string failures = string.Join('\n', options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)).Select(failure => failure.ErrorMessage));

        Assert.DoesNotContain(Secret, failures, StringComparison.Ordinal);
    }

    /// <summary>The records the SDK hands out and the requests it takes, whose members are named as secrets.</summary>
    public static TheoryData<string> SecretBearingRecords
        => [.. SecretRecords().Select(type => type.FullName!).Order(StringComparer.Ordinal)];

    /// <summary>
    /// A record's compiler-written <c>ToString()</c> prints every property, so an entity or request holding a PSK, a password
    /// or a token would print it (<c>new ZabbixHost { TlsPsk = … }.ToString()</c> would contain the key) into an exception
    /// message, an assertion, a debugger, a <c>"$host"</c> in PowerShell. So the generator emits <c>[ZabbixSecret]</c> on
    /// the secret fields (the description knows them: the module turns them into <c>SecureString</c> parameters) and a
    /// <c>PrintMembers</c> that skips classified members; <c>ZabbixProxyTask</c> does the same by hand.
    /// </summary>
    [Theory]
    [MemberData(nameof(SecretBearingRecords))]
    public void A_record_s_text_form_never_carries_a_secret_member(string typeName)
    {
        Type type = SecretRecords().Single(candidate => candidate.FullName == typeName);
        object record = CreateWithSecrets(type);

        Assert.DoesNotContain(Secret, record.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_record_probe_finds_the_generated_secret_bearing_records()
        => Assert.Contains(SecretRecords(), type => type == typeof(ZabbixHost));

    // ================================================================================================================

    private static async Task SendAsync(Harness harness, string method)
        => await harness.Transport.SendAsync(method, new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken);

    private static HttpResponseMessage Ok(string result, long id)
        => Json($$"""{"jsonrpc":"2.0","result":{{result}},"id":{{id.ToString(CultureInfo.InvariantCulture)}}}""");

    private static HttpResponseMessage Error(int code, string message, string data, long id)
        => Json($$"""{"jsonrpc":"2.0","error":{"code":{{code.ToString(CultureInfo.InvariantCulture)}},"message":{{JsonSerializer.Serialize(message)}},"data":{{JsonSerializer.Serialize(data)}}},"id":{{id.ToString(CultureInfo.InvariantCulture)}}}""");

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Fails with every place <paramref name="secret"/> surfaced.</summary>
    private static void AssertNoSecret(FakeLogCollector logs, SpanCapture spans, IEnumerable<Exception> exceptions, string secret)
    {
        List<string> leaks = [];
        foreach (FakeLogRecord record in logs.GetSnapshot())
        {
            if (record.Message.Contains(secret, StringComparison.Ordinal)) leaks.Add($"log {record.Id} message: {record.Message}");
            foreach (KeyValuePair<string, string?> pair in record.StructuredState ?? [])
                if (pair.Value?.Contains(secret, StringComparison.Ordinal) == true) leaks.Add($"log {record.Id} state {pair.Key}: {pair.Value}");
            if (record.Exception is { } logged && logged.ToString().Contains(secret, StringComparison.Ordinal)) leaks.Add($"log {record.Id} exception: {logged.GetType().Name}");
        }
        foreach (Activity span in spans.Stopped)
        {
            foreach (KeyValuePair<string, object?> tag in span.TagObjects)
                if (Convert.ToString(tag.Value, CultureInfo.InvariantCulture)?.Contains(secret, StringComparison.Ordinal) == true) leaks.Add($"span {span.OperationName} tag {tag.Key}");
            foreach (ActivityEvent @event in span.Events)
                foreach (KeyValuePair<string, object?> tag in @event.Tags)
                    if (Convert.ToString(tag.Value, CultureInfo.InvariantCulture)?.Contains(secret, StringComparison.Ordinal) == true) leaks.Add($"span {span.OperationName} event {@event.Name} tag {tag.Key}");
            if (span.StatusDescription?.Contains(secret, StringComparison.Ordinal) == true) leaks.Add($"span {span.OperationName} status");
        }
        foreach (Exception exception in exceptions)
            if (exception.ToString().Contains(secret, StringComparison.Ordinal)) leaks.Add($"exception {exception.GetType().Name}: {exception.Message}");

        DesignConformanceTests.AssertNone(leaks);
    }

    private static IEnumerable<Type> SecretRecords()
        => Libraries.Types
            .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && type.GetMethod("<Clone>$") is not null)
            .Where(type => SecretProperties(type).Any());

    private static IEnumerable<PropertyInfo> SecretProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string) && property.SetMethod is not null && SecretNames.IsMatch(property.Name));

    private static readonly System.Text.RegularExpressions.Regex SecretNames = new("(password|passwd|passphrase|psk|secret|accesstoken|refreshtoken)$|^token$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>An instance of <paramref name="type"/> with every secret-named string property set to <see cref="Secret"/>.</summary>
    private static object CreateWithSecrets(Type type)
    {
        ConstructorInfo constructor = type.GetConstructors().OrderBy(candidate => candidate.GetParameters().Length).First();
        object?[] arguments = [.. constructor.GetParameters().Select(parameter => parameter.ParameterType == typeof(string) ? (SecretNames.IsMatch(parameter.Name ?? "") ? Secret : "value") : DesignConformanceTests.Synthesize(parameter))];
        object record = constructor.Invoke(arguments);
        foreach (PropertyInfo property in SecretProperties(type)) property.SetValue(record, Secret);
        return record;
    }

    /// <summary>A server double that answers every JSON-RPC call with <see cref="Reply"/>(method, id).</summary>
    private sealed class SecretServer : HttpMessageHandler
    {
        public Func<string, long, HttpResponseMessage> Reply { get; set; } = (_, id) => Ok("[]", id);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using JsonDocument envelope = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return Reply(envelope.RootElement.GetProperty("method").GetString()!, envelope.RootElement.GetProperty("id").GetInt64());
        }
    }

    /// <summary>Every span any source stops while the test runs (the collection keeps the other tests' spans out).</summary>
    private sealed class SpanCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly ConcurrentQueue<Activity> _stopped = new();

        public SpanCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyCollection<Activity> Stopped => _stopped;

        public void Dispose() => _listener.Dispose();
    }
}

/// <summary>The secret probe listens to every activity source; it runs alone so its capture is its own.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AdversarialSecretIsolation
{
    public const string Name = "Adversarial secret probe";
}
