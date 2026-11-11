// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Community.Zabbix.Connector;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The connector stream is NDJSON read by the platform as top-level JSON values into typed records; the endpoint
/// answers a bad body 400 with a JSON <c>error</c> before writing anything, and a closed sink 503 (the server re-sends:
/// at least once).
/// </summary>
public sealed class ConnectorTests
{
    private const string Lines = """
        {"host":{"host":"web-01","name":"Web 01"},"groups":["Linux servers"],"item_tags":[{"tag":"component","value":"cpu"}],"itemid":100,"name":"CPU load","clock":1700000000,"ns":501,"value":0.42,"type":0}
        {"clock":1700000001,"ns":0,"value":1,"eventid":77,"name":"High load","severity":3,"hosts":[{"host":"web-01","name":"Web 01"}],"groups":["Linux servers"],"tags":[{"tag":"scope","value":"performance"}]}
        """;

    // cachehistory.c: a log value carries timestamp, source, severity and eventid (the log's) beside itemid.
    private const string LogLine = """{"host":{"host":"web-01","name":"Web 01"},"groups":[],"item_tags":[],"itemid":200,"name":"syslog","clock":1700000002,"ns":7,"timestamp":1699999999,"source":"kernel","severity":4,"eventid":1234,"value":"oops","type":2}""";

    [Fact]
    public void Lines_become_values_and_events_by_their_id_field()
    {
        IReadOnlyList<ZabbixConnectorRecord> records = ZabbixConnectorFeed.Parse(Lines);

        Assert.Equal(2, records.Count);
        ZabbixConnectorValue value = Assert.IsType<ZabbixConnectorValue>(records[0]);
        ZabbixConnectorEvent @event = Assert.IsType<ZabbixConnectorEvent>(records[1]);
        Assert.Equal(100UL, value.ItemId);
        Assert.Equal("web-01", value.Host!.HostName);
        Assert.Equal(0.42, value.Value.GetDouble());
        Assert.Equal(new ZabbixClock(1_700_000_000, 501), value.Exact);
        Assert.Equal("cpu", value.ItemTags![0].Value);
        Assert.Equal(77UL, @event.EventId);
        Assert.Equal(ZabbixSeverity.Average, @event.Severity);
        Assert.Equal(ZabbixItemValueType.Float, value.Type);
        Assert.Equal("performance", @event.Tags![0].Value);
        // the connector's tags are the API's own tag record, so one helper reads both
        Assert.IsType<ZabbixTag>(@event.Tags[0]);
        Assert.IsType<ZabbixTag>(value.ItemTags[0]);
    }

    [Fact]
    public void A_log_value_keeps_its_log_fields_and_stays_a_value()
    {
        ZabbixConnectorValue log = Assert.IsType<ZabbixConnectorValue>(ZabbixConnectorFeed.Parse(Encoding.UTF8.GetBytes(LogLine)));

        Assert.Equal(200UL, log.ItemId);
        Assert.Equal(1_699_999_999, log.Timestamp);
        Assert.Equal("kernel", log.Source);
        Assert.Equal(4, log.Severity);                                                    // a log's own severity, not a trigger's
        Assert.Equal(ZabbixItemValueType.Log, log.Type);
        Assert.Equal(1234, log.LogEventId);
        Assert.Equal("oops", log.Value.GetString());
    }

    [Theory]
    [InlineData("""{"neither":true}""")]
    [InlineData("null")]
    [InlineData("[1]")]
    public void A_line_that_is_neither_a_value_nor_an_event_is_refused(string line)
        => Assert.Throws<JsonException>(() => ZabbixConnectorFeed.Parse(line));

    [Fact]
    public async Task A_body_is_read_as_top_level_values_off_a_stream()
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(Lines + "\n" + LogLine + "\n"));

        List<ZabbixConnectorRecord> records = await ZabbixConnectorFeed.ReadAsync(body, TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([typeof(ZabbixConnectorValue), typeof(ZabbixConnectorEvent), typeof(ZabbixConnectorValue)], records.Select(record => record.GetType()));
    }

    private static async Task<(WebApplication App, Uri Endpoint, FakeLogCollector Logs)> HostAsync(ChannelWriter<ZabbixConnectorRecord> sink, ZabbixConnectorOptions? options = null)
    {
        var logs = new FakeLogCollector();
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ILoggerProvider>(_ => new FakeLoggerProvider(logs));   // the container owns and disposes it
        WebApplication app = builder.Build();
        app.MapZabbixConnector("/zabbix/connector", sink, options ?? new ZabbixConnectorOptions { AllowAnonymous = true });
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new Uri(address + "/zabbix/connector"), logs);
    }

    [Fact]
    public async Task The_endpoint_takes_a_post_into_the_channel_and_answers_200()
    {
        Channel<ZabbixConnectorRecord> sink = Channel.CreateBounded<ZabbixConnectorRecord>(16);
        (WebApplication app, Uri endpoint, _) = await HostAsync(sink.Writer);
        await using (app)
        {
            using var http = new HttpClient();

            using HttpResponseMessage ok = await http.PostAsync(endpoint, new StringContent(Lines, Encoding.UTF8, "application/x-ndjson"), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.IsType<ZabbixConnectorValue>(await sink.Reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.IsType<ZabbixConnectorEvent>(await sink.Reader.ReadAsync(TestContext.Current.CancellationToken));
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_bad_line_is_answered_400_with_a_json_error_and_nothing_is_written()
    {
        Channel<ZabbixConnectorRecord> sink = Channel.CreateUnbounded<ZabbixConnectorRecord>();
        (WebApplication app, Uri endpoint, FakeLogCollector logs) = await HostAsync(sink.Writer);
        await using (app)
        {
            using var http = new HttpClient();

            using HttpResponseMessage bad = await http.PostAsync(endpoint, new StringContent(Lines + "\n{not json\n", Encoding.UTF8, "application/x-ndjson"), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            using JsonDocument error = JsonDocument.Parse(await bad.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.False(string.IsNullOrEmpty(error.RootElement.GetProperty("error").GetString()));
            Assert.False(sink.Reader.TryRead(out _));                                             // the good lines before it were not written
            FakeLogRecord rejected = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 6301);
            Assert.Equal(LogLevel.Warning, rejected.Level);
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_closed_sink_is_answered_503_so_the_server_re_sends()
    {
        Channel<ZabbixConnectorRecord> sink = Channel.CreateUnbounded<ZabbixConnectorRecord>();
        sink.Writer.Complete();
        (WebApplication app, Uri endpoint, FakeLogCollector logs) = await HostAsync(sink.Writer);
        await using (app)
        {
            using var http = new HttpClient();

            using HttpResponseMessage closed = await http.PostAsync(endpoint, new StringContent(Lines, Encoding.UTF8, "application/x-ndjson"), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, closed.StatusCode);
            using JsonDocument error = JsonDocument.Parse(await closed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(error.RootElement.TryGetProperty("error", out _));
            FakeLogRecord refused = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 6302);
            Assert.Equal(LogLevel.Warning, refused.Level);
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void An_endpoint_that_names_no_admission_rule_is_refused_at_mapping()
    {
        WebApplication app = WebApplication.CreateSlimBuilder().Build();
        Channel<ZabbixConnectorRecord> sink = Channel.CreateUnbounded<ZabbixConnectorRecord>();

        Assert.Throws<ArgumentException>(() => app.MapZabbixConnector("/zabbix/connector", sink.Writer, new ZabbixConnectorOptions()));
    }

    [Fact]
    public async Task A_post_without_the_bearer_token_is_answered_401_and_nothing_is_written()
    {
        Channel<ZabbixConnectorRecord> sink = Channel.CreateUnbounded<ZabbixConnectorRecord>();
        (WebApplication app, Uri endpoint, FakeLogCollector logs) = await HostAsync(sink.Writer, new ZabbixConnectorOptions { BearerToken = "s3cret-token" });
        await using (app)
        {
            using var http = new HttpClient();
            using var missing = new StringContent(Lines, Encoding.UTF8, "application/x-ndjson");
            using var wrong = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(Lines, Encoding.UTF8, "application/x-ndjson") };
            wrong.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "s3cret-tokeN");
            using var right = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(Lines, Encoding.UTF8, "application/x-ndjson") };
            right.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "s3cret-token");

            using HttpResponseMessage none = await http.PostAsync(endpoint, missing, TestContext.Current.CancellationToken);
            using HttpResponseMessage bad = await http.SendAsync(wrong, TestContext.Current.CancellationToken);
            Assert.False(sink.Reader.TryRead(out _));
            using HttpResponseMessage ok = await http.SendAsync(right, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
            Assert.Equal("Bearer", Assert.Single(none.Headers.WwwAuthenticate).Scheme);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.True(sink.Reader.TryRead(out _));
            Assert.Equal(2, logs.GetSnapshot().Count(record => record.Id.Id == 6303));
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_post_from_a_peer_outside_the_list_is_answered_403()
    {
        Channel<ZabbixConnectorRecord> sink = Channel.CreateUnbounded<ZabbixConnectorRecord>();
        var options = new ZabbixConnectorOptions();
        options.AllowedPeers.Add("192.0.2.0/24");
        (WebApplication app, Uri endpoint, _) = await HostAsync(sink.Writer, options);
        await using (app)
        {
            using var http = new HttpClient();
            using var body = new StringContent(Lines, Encoding.UTF8, "application/x-ndjson");

            using HttpResponseMessage refused = await http.PostAsync(endpoint, body, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.False(sink.Reader.TryRead(out _));
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
