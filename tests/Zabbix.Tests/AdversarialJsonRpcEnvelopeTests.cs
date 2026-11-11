// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>
/// The JSON-RPC envelope against what a broken front or a lying server sends: members written twice, an error code as
/// text, a reply at and past the size cap, a reply with the wrong content type, a token that cannot travel, a version
/// answered as <c>7.4</c> or as garbage, and a method the server's version does not have. Each is answered by a designed
/// failure, or read as the reply it is, never by an accessor's exception or a wrong answer.
/// </summary>
public sealed class AdversarialJsonRpcEnvelopeTests
{
    /// <summary>Answers every request with <paramref name="body"/>, its <c>ID</c> replaced by the request's id.</summary>
    private sealed class Replying(string body, string mediaType = "application/json", HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            using JsonDocument envelope = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            string id = envelope.RootElement.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture);
            return new HttpResponseMessage(status) { Content = new StringContent(body.Replace("ID", id, StringComparison.Ordinal), Encoding.UTF8, mediaType) };
        }
    }

    private static Task<JsonElement> CallAsync(Harness harness)
        => harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":ID,\"id\":999}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":999,\"result\":[],\"id\":ID}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"result\":[],\"result\":{\"hostid\":\"1\"},\"id\":ID}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32500,\"message\":\"a\"},\"error\":{\"code\":-32602,\"message\":\"b\"},\"id\":ID}")]
    public async Task A_reply_that_writes_a_member_twice_is_not_the_reply(string body)
    {
        using var handler = new Replying(body);
        using Harness harness = TransportHarness.Build(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => CallAsync(harness));
    }

    [Fact]
    public async Task An_error_whose_code_is_text_is_still_the_api_s_refusal_with_its_words_and_no_code()
    {
        using var handler = new Replying("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":\"-32602\",\"message\":\"Invalid params.\",\"data\":\"Not authorized.\"},\"id\":ID}");
        using Harness harness = TransportHarness.Build(handler);

        ZabbixApiException refused = await Assert.ThrowsAsync<ZabbixApiException>(() => CallAsync(harness));

        Assert.Equal(0, refused.Code);
        Assert.Equal(ZabbixApiErrorKind.Unknown, refused.Kind);                                 // the kind needs the code: not guessed
        Assert.Equal("Not authorized.", refused.Detail);
    }

    [Theory]
    [InlineData(4000, false)]
    [InlineData(4096, false)]
    [InlineData(4097, true)]
    [InlineData(64 * 1024, true)]
    public async Task A_reply_is_read_up_to_the_size_cap_and_refused_past_it(int size, bool refused)
    {
        string prefix = "{\"jsonrpc\":\"2.0\",\"result\":\"", suffix = "\",\"id\":1}";
        string body = prefix + new string('x', size - prefix.Length - suffix.Length) + suffix;
        Assert.Equal(size, Encoding.UTF8.GetByteCount(body));
        using var handler = new Replying(body);
        using Harness harness = TransportHarness.Build(handler, configure: services => services.Configure<ZabbixClientOptions>(Options.DefaultName, options => options.MaxResponseContentBytes = 4096));

        Exception? failure = await Record.ExceptionAsync(() => CallAsync(harness));

        if (refused) Assert.IsType<InvalidDataException>(failure);
        else Assert.Null(failure);
    }

    [Theory]
    [InlineData("text/html", HttpStatusCode.OK, null)]
    [InlineData("text/plain", HttpStatusCode.OK, null)]
    [InlineData("application/json", HttpStatusCode.InternalServerError, typeof(ZabbixApiException))]
    [InlineData("text/html", HttpStatusCode.InternalServerError, typeof(HttpRequestException))]
    public async Task An_envelope_is_read_by_its_body_and_a_status_counts_only_when_the_body_is_not_one(string mediaType, HttpStatusCode status, Type? expected)
    {
        using var handler = new Replying("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32500,\"message\":\"Application error.\",\"data\":\"No permissions.\"},\"id\":ID}", mediaType, status);
        using Harness harness = TransportHarness.Build(handler);

        Exception? failure = await Record.ExceptionAsync(() => CallAsync(harness));

        // A 200 with any content type is the frontend's own answer, read as the envelope it is: here, its error.
        Assert.IsType(expected ?? typeof(ZabbixApiException), failure);
    }

    [Theory]
    [InlineData("s3cr3t-77\n")]
    [InlineData("s3cr3t 77")]
    [InlineData(" s3cr3t-77")]
    [InlineData("s3cr3t-77\u0000")]
    public void A_token_that_cannot_travel_as_a_bearer_is_refused_when_it_is_given_and_when_it_is_configured(string token)
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(() => new ZabbixApiTokenCredential(token));
        Assert.DoesNotContain("s3cr3t", refused.Message, StringComparison.Ordinal);                   // the secret is never in the message

        var options = new ZabbixClientOptions { Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"), ApiToken = token };
        Assert.Contains(options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)), result => result.MemberNames.Contains(nameof(ZabbixClientOptions.ApiToken)));
    }

    [Theory]
    [InlineData("7.4", true)]
    [InlineData("7.4.0", true)]
    [InlineData("7.4.15", true)]
    [InlineData("7.0.31", false)]
    public async Task A_version_answered_without_its_patch_gates_as_its_major_and_minor(string answered, bool supported)
    {
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", "\"" + answered + "\"").On("discoveryruleprototype.get", "[]");
        using Harness harness = TransportHarness.Build(api);
        ZabbixClient client = harness.Provider.GetRequiredService<ZabbixClient>();

        Exception? failure = await Record.ExceptionAsync(() => client.DiscoveryRulePrototypes.GetAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(supported, failure is null);
        if (!supported) Assert.IsType<ZabbixVersionException>(failure);
    }

    [Theory]
    [InlineData("\"garbage\"")]
    [InlineData("\"8\"")]
    [InlineData("\"\"")]
    [InlineData("7.4")]
    [InlineData("null")]
    public async Task A_version_that_is_not_one_is_invalid_data_and_is_asked_again_next_time(string answered)
    {
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", answered);
        using Harness harness = TransportHarness.Build(api);
        ZabbixClient client = harness.Provider.GetRequiredService<ZabbixClient>();

        Exception first = await Assert.ThrowsAnyAsync<Exception>(() => client.GetApiVersionAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetApiVersionAsync(TestContext.Current.CancellationToken));

        Assert.True(first is InvalidDataException or JsonException, first.GetType().FullName);
        Assert.Equal(2, api.ParamsOf("apiinfo.version").Count());                               // a failed read is not kept
    }

    [Fact]
    public async Task A_method_the_server_s_version_does_not_have_is_refused_before_anything_but_the_version_is_sent()
    {
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", "\"7.0.31\"");
        using Harness harness = TransportHarness.Build(api);
        ZabbixClient client = harness.Provider.GetRequiredService<ZabbixClient>();

        ZabbixVersionException refused = await Assert.ThrowsAsync<ZabbixVersionException>(() => client.CepRules.GetAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(["apiinfo.version"], api.Calls.Select(call => call.Method));
        Assert.Equal("version_unsupported", Zabbix.Diagnostics.ZabbixErrorType.Of(refused));
    }

    [Fact]
    public async Task A_server_that_lies_about_its_version_answers_for_itself_with_its_own_refusal()
    {
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", "\"8.0.0\"")
            .OnError("ceprule.get", -32601, "Method not found.", "Incorrect API ceprule.");
        using Harness harness = TransportHarness.Build(api);
        ZabbixClient client = harness.Provider.GetRequiredService<ZabbixClient>();

        ZabbixApiException refused = await Assert.ThrowsAsync<ZabbixApiException>(() => client.CepRules.GetAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ZabbixApiErrorKind.MethodNotFound, refused.Kind);
    }

    [Fact]
    public async Task A_login_whose_session_is_not_text_is_a_failed_read_not_a_session()
    {
        using ScriptedApi api = new ScriptedApi().On("user.login", "{\"userid\":\"1\",\"sessionid\":5,\"mfaid\":0}").On("host.get", "[]");
        using Harness harness = TransportHarness.Build(api, token: null, configure: services => services.AddKeyedSingleton<ZabbixCredential>(Options.DefaultName, new ZabbixSessionCredential("Admin", "zabbix")));

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => CallAsync(harness));

        Assert.True(failure is JsonException or InvalidDataException, failure.GetType().FullName);
        Assert.Empty(api.ParamsOf("host.get"));                                                  // nothing is sent without a session
    }
}
