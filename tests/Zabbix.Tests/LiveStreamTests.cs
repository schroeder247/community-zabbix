// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Community.Zabbix.Connector;
using Community.Zabbix.Hosting;
using Community.Zabbix.Protocol;
using Community.Zabbix.Storage;
using Community.Zabbix.Storage.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Community.Zabbix.Tests;

/// <summary>
/// What flows out of a server, end to end: a streaming connector into the SDK's connector endpoint (the values must be
/// the ones <c>history.get</c> holds), a media-type webhook into a receiver (the alert must read as sent), and the SDK's
/// own pipeline — a watcher on one item, a store, the send loop and the sender into another — read back to the
/// nanosecond, on the in-memory store and on the EF store over PostgreSQL.
/// </summary>
public sealed class LiveStreamTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_streaming_connector_delivers_the_values_history_get_holds_to_the_connector_endpoint(string version)
    {
        (LiveTarget target, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        target.RequireFeature("connector", "StartConnectors must be above 0 for a connector to run");
        await using LiveScope scope = LiveScope.Open(version, "connector");
        Channel<ZabbixConnectorRecord> received = Channel.CreateUnbounded<ZabbixConnectorRecord>();
        await using WebApplication receiver = await ReceiverAsync(app => app.MapZabbixConnector("/zbx/connector", received.Writer, new ZabbixConnectorOptions { BearerToken = scope.Run }));
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "connector", [("zbxtest.float", ZabbixItemValueType.Float)], itemExtra: $$""","tags":[{"tag":"zbx-run","value":"{{scope.Run}}"}]""");
        string connector = (await scope.Server.RpcAsync("connector.create", new
        {
            name = scope.Name("connector"),
            url = $"http://{LiveMatrix.HostFromServer}:{Port(receiver)}/zbx/connector",
            data_type = 0,
            authtype = 5,                                                                       // ZBX_HTTP_AUTH_BEARER: the endpoint fails closed
            token = scope.Run,
            item_value_type = 1,                                                                // ZBX_CONNECTOR_ITEM_VALUE_TYPE_FLOAT
            tags_evaltype = 0,
            tags = new[] { new { tag = "zbx-run", @operator = 0, value = scope.Run } },
        })).Id("connectorids");
        scope.Delete("connector", connector);
        long second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var sender = new ZabbixSender([trapper]);
        int attempt = 0;
        ZabbixValue[] Values() => [new(host.Host, "zbxtest.float", "1.25", new ZabbixClock(second, attempt * 1000 + 111)), new(host.Host, "zbxtest.float", "2.5", new ZabbixClock(second, attempt * 1000 + 222))];

        // Two observables, so a timeout names which step did not happen (a matrix run can wait 60 s with the connector
        // worker receiving nothing, while the same test alone passes in seconds).
        // First the trapper takes the values (the item is in the configuration cache) ...
        await LiveApi.EventuallyAsync(async () =>
            await sender.SendAsync(Values(), Cancel) is { Processed: 2 } response ? response : null, "the trapper to take zbxtest.float's values");
        // ... then the connector, which takes only values the history syncer writes once the connector is in the cache
        // too: each send is new values (a fresh ns), never a duplicate of a stored one, until one is streamed.
        await LiveApi.EventuallyAsync(async () =>
        {
            if (received.Reader.TryPeek(out _)) return "streamed";
            attempt++;
            await sender.SendAsync(Values(), Cancel);
            return null;
        }, "the connector to stream a value the trapper took");
        var streamed = new List<ZabbixConnectorValue>();
        while (received.Reader.TryRead(out ZabbixConnectorRecord? record))
            if (record is ZabbixConnectorValue value && value.ItemId == host["zbxtest.float"].ItemId) streamed.Add(value);
        IReadOnlyList<ZabbixHistoryRecord> stored = await LiveFixtures.HistoryAsync(scope.Server, host["zbxtest.float"], streamed.Count);

        Assert.NotEmpty(streamed);
        Assert.All(streamed, v =>
        {
            Assert.Equal(host.Host, v.Host?.HostName);
            Assert.Contains(stored, r => r.Clock == v.Clock && double.Parse(r.Value!, CultureInfo.InvariantCulture) == v.Value.GetDouble());
            Assert.Contains(v.ItemTags ?? [], t => t.Tag == "zbx-run" && t.Value == scope.Run);
        });
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_problem_runs_its_action_through_a_webhook_media_type_to_a_receiver_and_the_alert_reads_as_sent(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "webhook");
        Channel<string> posts = Channel.CreateUnbounded<string>();
        await using WebApplication receiver = await ReceiverAsync(app => app.MapPost("/zbx/webhook", async (HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            await posts.Writer.WriteAsync(await reader.ReadToEndAsync(http.RequestAborted), http.RequestAborted);
            return Results.Ok();
        }));
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "webhook", [("zbxtest.fire", ZabbixItemValueType.Unsigned)]);
        await scope.Server.RpcAsync("trigger.create", new { description = scope.Name("trigger"), expression = $"last(/{host.Host}/zbxtest.fire)>0", priority = 2 });
        string mediaType = (await scope.Server.RpcAsync("mediatype.create", new
        {
            name = scope.Name("webhook"),
            type = 4,
            status = 0,                                                                         // the schema's default is 1, disabled
            script = "var p = JSON.parse(value); var r = new HttpRequest(); r.addHeader('Content-Type: application/json'); r.post(p.URL, JSON.stringify({subject: p.Subject, message: p.Message})); if (r.getStatus() != 200) throw 'status ' + r.getStatus(); return 'OK';",
            parameters = new[] { new { name = "URL", value = $"http://{LiveMatrix.HostFromServer}:{Port(receiver)}/zbx/webhook" }, new { name = "Subject", value = "{ALERT.SUBJECT}" }, new { name = "Message", value = "{ALERT.MESSAGE}" } },
            timeout = "10s",
        })).Id("mediatypeids");
        scope.Delete("mediatype", mediaType);
        string group = (await scope.Server.RpcAsync("usergroup.create", new { name = scope.Name("usergroup"), hostgroup_rights = new[] { new { id = host.GroupId, permission = 2 } } })).Id("usrgrpids");
        scope.Delete("usergroup", group);
        JsonElement role = await scope.Server.RpcAsync("role.get", new { output = new[] { "roleid" }, filter = new { name = new[] { "User role" } } });
        string user = (await scope.Server.RpcAsync("user.create", new
        {
            username = scope.Name("user"),
            passwd = "Zbx-" + Guid.NewGuid().ToString("N"),
            roleid = role[0].GetProperty("roleid").GetString(),
            usrgrps = new[] { new { usrgrpid = group } },
            medias = new[] { new { mediatypeid = mediaType, sendto = "zbx", active = 0, severity = 63, period = "1-7,00:00-24:00" } },
        })).Id("userids");
        scope.Delete("user", user);
        string subject = scope.Name("subject");
        string action = (await scope.Server.RpcAsync("action.create", new
        {
            name = scope.Name("action"),
            eventsource = 0,
            filter = new { evaltype = 0, conditions = new[] { new { conditiontype = 0, @operator = 0, value = host.GroupId } } },
            operations = new[] { new { operationtype = 0, opmessage = new { default_msg = 0, subject, message = "{EVENT.NAME} on {HOST.HOST}", mediatypeid = mediaType }, opmessage_usr = new[] { new { userid = user } } } },
        })).Id("actionids");
        scope.Delete("action", action);

        // An action reaches the escalator through the configuration cache: a problem raised before the action is synced
        // matches nothing (seen on the 7.4 lab, CacheUpdateFrequency=1, where the item is known a second before the
        // action is). So recover and raise again until the action has made an alert of one of the problems.
        using var sender = new ZabbixSender([trapper]);
        await LiveFixtures.SendWhenKnownAsync(sender, [new ZabbixValue(host.Host, "zbxtest.fire", "0", DateTimeOffset.UtcNow)]);
        JsonElement alert = await LiveApi.EventuallyAsync<JsonElement?>(async () =>
        {
            JsonElement alerts = await scope.Server.RpcAsync("alert.get", new { actionids = new[] { action }, output = new[] { "status", "error", "subject", "mediatypeid" } });
            if (alerts.GetArrayLength() > 0) return alerts[0].GetProperty("status").GetString() != "0" ? alerts[0] : null;   // 0: not sent yet
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await sender.SendAsync([new ZabbixValue(host.Host, "zbxtest.fire", "0", now), new ZabbixValue(host.Host, "zbxtest.fire", "1", now.AddMilliseconds(1))], Cancel);
            await Task.Delay(TimeSpan.FromSeconds(2), TimeProvider.System, Cancel);             // one raise per two cache syncs
            return null;
        }, "the action to make an alert of a problem and the alerter to process it", TimeSpan.FromSeconds(120)) ?? default;
        Assert.True(posts.Reader.TryRead(out string? post), $"the alert reads as processed ({alert}) but the receiver got no post");

        using JsonDocument body = JsonDocument.Parse(post);
        Assert.Equal(subject, body.RootElement.GetProperty("subject").GetString());
        Assert.Equal($"{scope.Name("trigger")} on {host.Host}", body.RootElement.GetProperty("message").GetString());
        Assert.Equal(("1", ""), (alert.GetProperty("status").GetString(), alert.GetProperty("error").GetString()));   // ALERT_STATUS_SENT
        Assert.Equal(mediaType, alert.GetProperty("mediatypeid").GetString());
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public Task A_watcher_feeds_a_memory_store_the_send_loop_drains_to_the_trapper_to_the_nanosecond(string version)
        => PipelineAsync(version, "pipeline-memory", _ => new ZabbixMemoryStore<ZabbixValue>());

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The identifier is ZabbixEntityFrameworkStore's generated table name, [a-z0-9_] only.")]
    public async Task A_watcher_feeds_an_ef_store_on_postgres_the_send_loop_drains_to_the_trapper_to_the_nanosecond(string version)
    {
        if (string.IsNullOrWhiteSpace(Lab.Postgres)) Assert.Skip("ZABBIX_TEST_POSTGRES is not set (tests/lab/lab.sh start pg)");
        string? table = null;
        try
        {
            await PipelineAsync(version, "pipeline-ef", run =>
            {
                var store = new ZabbixEntityFrameworkStore<ZabbixValue>(o => o.UseNpgsql(Lab.Postgres), services: new ZabbixStoreServices { Name = run.Replace('-', '_') });
                table = store.Table;
                return store;
            });
        }
        finally
        {
            if (table is not null)
            {
                await using var connection = new NpgsqlConnection(Lab.Postgres);
                await connection.OpenAsync(Cancel);
                await using NpgsqlCommand drop = connection.CreateCommand();
                drop.CommandText = $"DROP TABLE IF EXISTS \"{table}\"";                          // the store's generated [a-z0-9_] name
                await drop.ExecuteNonQueryAsync(Cancel);
            }
        }
    }

    /// <summary>
    /// A watcher on one host's item feeds <paramref name="store"/>, each value re-addressed to another host's item; the
    /// sender drains the store through the send loop. What the second item holds must be the first item's values with their
    /// clocks to the nanosecond, in order, once each.
    /// </summary>
    private static async Task PipelineAsync(string version, string what, Func<string, ZabbixStore<ZabbixValue>> store)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, what);
        LiveFixtures.LiveHost source = await LiveFixtures.HostAsync(scope, "source", [("zbxtest.src", ZabbixItemValueType.Float)]);
        LiveFixtures.LiveHost target = await LiveFixtures.HostAsync(scope, "target", [("zbxtest.dst", ZabbixItemValueType.Float)]);
        var sender = new ZabbixSender([trapper]);
        await LiveFixtures.SendWhenKnownAsync(sender, [new ZabbixValue(target.Host, "zbxtest.dst", "0", new ZabbixClock(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(), 0))]);
        await LiveFixtures.SendWhenKnownAsync(sender, [new ZabbixValue(source.Host, "zbxtest.src", "0", new ZabbixClock(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(), 0))]);
        await using ZabbixStore<ZabbixValue> buffer = store(scope.Run);
        await using IZabbixWatcher watcher = scope.Server.Hosts[source.Host].Items["zbxtest.src"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(1)) { AllowedLateness = TimeSpan.FromSeconds(30) });
        ZabbixItemKey dst = ZabbixItemKey.Parse("zbxtest.dst");
        // Attached before anything is sent, and sent only after the first poll: a watcher starts at its first poll.
        await using IAsyncEnumerator<ZabbixValue> reader = watcher.GetAsyncEnumerator(Cancel);
        await LiveApi.EventuallyAsync(() => Task.FromResult<string?>(watcher.SucceededPolls > 0 ? "polled" : null), "the watcher's first poll");
        long second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ZabbixValue[] sent = [.. Enumerable.Range(1, 3).Select(i => new ZabbixValue(source.Host, "zbxtest.src", (i * 1.5).ToString(CultureInfo.InvariantCulture), new ZabbixClock(second, i * 111_111_111)))];

        int taken = 0;
        Task pump = Task.Run(async () =>
        {
            while (taken < sent.Length && await reader.MoveNextAsync())
            {
                ZabbixValue value = reader.Current;
                if (value.Clock.Seconds < second) continue;                                       // the fixture's probe value
                await buffer.WriteAsync(value with { HostName = target.Host, Key = dst }, Cancel);
                Interlocked.Increment(ref taken);
            }
            buffer.Complete();
        }, Cancel);
        using var wire = new ZabbixWireClient(trapper.ToString());
        Task<long> drain = wire.DrainAsync(buffer, new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { FlushInterval = TimeSpan.FromMilliseconds(200) } }, cancellationToken: Cancel);
        await sender.SendAsync(sent, Cancel);
        await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(60), TimeProvider.System, Cancel));
        Assert.True(pump.IsCompleted, $"the watcher yielded {Volatile.Read(ref taken)} of {sent.Length} value(s) in 60 s ({watcher.SucceededPolls} poll(s), {watcher.FailedPolls} failed)");
        await pump;
        long drained = await drain.WaitAsync(TimeSpan.FromSeconds(60), TimeProvider.System, Cancel);
        sender.Dispose();                                                                   // after the drain that uses it

        IReadOnlyList<ZabbixHistoryRecord> arrived = [.. (await LiveFixtures.HistoryAsync(scope.Server, target["zbxtest.dst"], sent.Length + 1)).Where(r => r.ClockExact?.Seconds >= second)];
        Assert.Equal(sent.Length, drained);
        Assert.Equal(sent.Select(v => (double.Parse(v.Value, CultureInfo.InvariantCulture), v.Clock.Seconds, v.Clock.Nanoseconds)),
                     arrived.Select(r => (double.Parse(r.Value!, CultureInfo.InvariantCulture), r.ClockExact!.Value.Seconds, r.ClockExact!.Value.Nanoseconds)));
    }

    private static async Task<WebApplication> ReceiverAsync(Action<WebApplication> map)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://{LiveHosting.ListenAddress}:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        map(app);
        await app.StartAsync(Cancel);
        return app;
    }

    private static int Port(WebApplication app)
        => new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)).Port;
}
