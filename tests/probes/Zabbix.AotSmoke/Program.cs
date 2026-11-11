// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Community.Zabbix;
using Community.Zabbix.Listening;

// Every entry point, composed by a host and without one: the API client (generated records and the JSON context), the
// agents, one with a trapper listener, the proxies, a listener of its own, and the wire client, constructed, registered by
// name and built by their builders.
// Nothing listens on port 9, so each call ends in a connection failure, which proves the code ran to the socket under
// Native AOT.
var services = new ServiceCollection();
services.AddZabbixClient("smoke").WithEndpoint("http://127.0.0.1:9/api_jsonrpc.php");
services.AddZabbixPushAgent("relay").AddTrapper(10099, o => o.AllowedPeers.Add("127.0.0.1")).AddServer("127.0.0.1:9");
services.AddZabbixWireClient("smoke").WithServer("127.0.0.1:9").WithTimeout(TimeSpan.FromSeconds(5));
await using ServiceProvider provider = services.BuildServiceProvider();
ZabbixClient client = provider.GetRequiredService<IZabbixClientFactory>().Create("smoke");
try
{
    var hosts = await client.Hosts.GetAsync(q =>
    {
        q.Output = [ZabbixHostFields.HostId, ZabbixHostFields.VisibleName];
        q.Sort = [ZabbixHostFields.VisibleName.Descending(), ZabbixHostFields.HostId];
    }).ConfigureAwait(false);
    Console.WriteLine($"hosts: {hosts.Count}");
}
catch (HttpRequestException e)
{
    Console.WriteLine($"api: {e.GetType().Name}");
}

// A write as callers shape it: the record a create's constructor builds and its lambda fills, with a host's interfaces
// (the first of each type the main one) and a tag from a pair, written by the generated JSON context; then sent, as a create
// and as a node's update.
var write = new ZabbixHostCreateRequest("smoke", [1UL]) { Interfaces = [ZabbixInterface.Agent(IPAddress.Loopback), ZabbixInterface.Agent("smoke.example")] };
write.Tags = [("env", "smoke")];
string written = JsonSerializer.Serialize(write, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<ZabbixHostCreateRequest>)ZabbixJson.Options.GetTypeInfo(typeof(ZabbixHostCreateRequest)));
if (!written.Contains("""{"type":1,"ip":"127.0.0.1","dns":"","port":"10050","useip":1,"main":1}""", StringComparison.Ordinal)
    || !written.Contains("\"main\":0", StringComparison.Ordinal) || !written.Contains("""[{"tag":"env","value":"smoke"}]""", StringComparison.Ordinal))
{
    Console.WriteLine($"aot smoke: a host's create wrote {written}");
    return 1;
}
try
{
    await client.Hosts.CreateAsync("smoke", [1UL], h => h.Tags = [("env", "smoke")]).ConfigureAwait(false);
}
catch (HttpRequestException e)
{
    Console.WriteLine($"create: {e.GetType().Name}");
}
try
{
    await client.Items[1].UpdateAsync(i => i.Delay = TimeSpan.FromMinutes(5)).ConfigureAwait(false);
}
catch (HttpRequestException e)
{
    Console.WriteLine($"update: {e.GetType().Name}");
}

// A count adds countOutput to the parameters it sends, a host's node is one type by id and by name with its items an indexer,
// and the client the container shares survives a caller's dispose.
try
{
    long count = await client.Hosts.CountAsync(q => q.Search.VisibleName = "smoke").ConfigureAwait(false);
    Console.WriteLine($"count: {count}");
}
catch (HttpRequestException e)
{
    Console.WriteLine($"count: {e.GetType().Name}");
}
ZabbixHostNode byId = client.Hosts[10084];
if (byId.GetType() != client.Hosts["smoke"].GetType() || byId.Items["agent.ping"].Key.ToString() != "agent.ping")
{
    Console.WriteLine("aot smoke: a host's node by id isn't the node its name reaches");
    return 1;
}
await client.DisposeAsync().ConfigureAwait(false);                       // the container's: it keeps running
try
{
    ZabbixValue? last = await byId.Items["agent.ping"].FindLastValueAsync().ConfigureAwait(false);
    Console.WriteLine($"node: {last}");
}
catch (HttpRequestException e)
{
    Console.WriteLine($"node: {e.GetType().Name}");
}

// The typed values as the generated JSON context writes and reads them: a get's fields and its sort, an item's key, its
// interval, its history and its allowed hosts, an interface's port from a macro, a proxy's port and timeout, a method, and an
// agent's schedule for an interval.
string queried = JsonSerializer.Serialize(
    new ZabbixHostGetParameters { Output = [ZabbixHostFields.HostId, ZabbixHostFields.VisibleName], Sort = [ZabbixHostFields.VisibleName.Descending(), ZabbixHostFields.HostId] },
    TypeInfo<ZabbixHostGetParameters>());
string itemWritten = JsonSerializer.Serialize(
    new ZabbixItemCreateRequest(1UL, "smoke", ZabbixItemType.Trapper, ZabbixItemKey.Create("smoke.key", "a", "b"), ZabbixItemValueType.Unsigned)
    {
        Delay = ZabbixInterval.Parse("1m;10s/1-5,09:00-18:00"),
        History = TimeSpan.FromDays(90),
        TrapperHosts = [IPNetwork.Parse("10.0.0.0/8"), IPAddress.IPv6Loopback, ZabbixPeer.Parse("{HOST.CONN}")],
    },
    TypeInfo<ZabbixItemCreateRequest>());
string portWritten = JsonSerializer.Serialize(ZabbixInterface.Agent(IPAddress.Loopback) with { Port = ZabbixPort.Parse("{$AGENT_PORT}") }, TypeInfo<ZabbixHostInterfaceRequest>());
ZabbixItem itemRead = JsonSerializer.Deserialize("""{"itemid":"7","key_":"vfs.fs.size[/,pfree]","delay":"{$DELAY}","history":"90d","trapper_hosts":"10.0.0.0/8, ::1"}""", TypeInfo<ZabbixItem>())!;
ZabbixProxy proxyRead = JsonSerializer.Deserialize("""{"proxyid":"3","port":10051,"timeout_zabbix_agent":"1y"}""", TypeInfo<ZabbixProxy>())!;
ZabbixHostGetParameters sortRead = JsonSerializer.Deserialize("""{"sortfield":["name","hostid"],"sortorder":["DESC"]}""", TypeInfo<ZabbixHostGetParameters>())!;
if (!queried.Contains("""{"output":["hostid","name"],"sortfield":["name","hostid"],"sortorder":["DESC","ASC"]}""", StringComparison.Ordinal)
    || !itemWritten.Contains("\"key_\":\"smoke.key[a,b]\"", StringComparison.Ordinal)
    || !itemWritten.Contains("\"delay\":\"1m;10s/1-5,09:00-18:00\"", StringComparison.Ordinal)
    || !itemWritten.Contains("\"history\":\"90d\"", StringComparison.Ordinal)
    || !itemWritten.Contains("\"trapper_hosts\":\"10.0.0.0/8,::1,{HOST.CONN}\"", StringComparison.Ordinal)
    || !portWritten.Contains("\"port\":\"{$AGENT_PORT}\"", StringComparison.Ordinal)
    || itemRead.Key?.Parameters is not ["/", "pfree"] || itemRead.Delay?.ToString() != "{$DELAY}" || itemRead.History?.Length != TimeSpan.FromDays(90)
    || proxyRead.Port?.Number != 10051 || proxyRead.TimeoutZabbixAgent?.Length != TimeSpan.FromDays(365)
    || ZabbixApiMethod.Parse("HOST.GET") != ZabbixApiMethod.HostGet || !ZabbixApiMethods.IsIdempotent(ZabbixApiMethod.HostGet)
    || itemRead.TrapperHosts is not [{ Network: { } block }, { Network: { } loopback }] || block != IPNetwork.Parse("10.0.0.0/8") || loopback != IPNetwork.Parse("::1/128")
    || sortRead.Sort is not [{ Direction: System.ComponentModel.ListSortDirection.Descending } name, { Direction: System.ComponentModel.ListSortDirection.Ascending } hostId]
    || name.Field != ZabbixHostFields.VisibleName || hostId.Field != ZabbixHostFields.HostId
    || ZabbixInterval.Parse("1h;10s/1-5,09:00-18:00").GetNextCheck(DateTimeOffset.Parse("2026-09-23T08:30:00Z", System.Globalization.CultureInfo.InvariantCulture), TimeZoneInfo.Utc) != DateTimeOffset.Parse("2026-09-23T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture))
{
    Console.WriteLine($"aot smoke: typed values wrote {queried}, {itemWritten}, {portWritten}");
    return 1;
}
Console.WriteLine("typed values: written and read");

// The API client its builder binds from configuration, on a container of its own: the generated binder (its Resilience
// subsection retries at once), the options' validator and the same HTTP pipeline as the registered client's, read with a token
// passed by position.
IConfiguration apiSection = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Endpoint"] = "http://127.0.0.1:9/",
    ["MaxConnectionsPerServer"] = "4",
    ["Resilience:Retry:Delay"] = "00:00:00",
}).Build();
await using (ZabbixClient bound = ZabbixClient.CreateBuilder().Bind(apiSection).WithTimeout(ZabbixApiMethod.HostGet, TimeSpan.FromSeconds(30)).Build())
{
    if (bound.Endpoint.AbsoluteUri != "http://127.0.0.1:9/api_jsonrpc.php")
    {
        Console.WriteLine($"aot smoke: the builder bound the endpoint '{bound.Endpoint}'");
        return 1;
    }
    try
    {
        var hosts = await bound.Hosts.GetAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"built hosts: {hosts.Count}");
    }
    catch (HttpRequestException e)
    {
        Console.WriteLine($"built api: {e.GetType().Name}");
    }
}

await using var wire = new ZabbixWireClient("127.0.0.1:9");
try
{
    await wire.SendAsync("smoke", new ZabbixItemKey<long>("smoke.key"), 1L, CancellationToken.None).ConfigureAwait(false);
}
catch (System.Net.Sockets.SocketException e)
{
    Console.WriteLine($"sender: {e.GetType().Name}");
}

// The registered client, and one its builder binds from configuration: the generated binder and the options' validator.
IConfiguration wireSection = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Server"] = "127.0.0.1:9", ["BatchSize"] = "10" }).Build();
ZabbixWireClient registered = provider.GetRequiredService<IZabbixWireClientFactory>().Create("smoke");
await using ZabbixWireClient built = ZabbixWireClient.CreateBuilder().Bind(wireSection).WithTimeout(TimeSpan.FromSeconds(5)).Build();
var bindings = new ServiceCollection();
bindings.AddZabbixWireClient("bound").Bind(wireSection);
await using (ServiceProvider bound = bindings.BuildServiceProvider())
{
    ZabbixWireClientOptions boundOptions = bound.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ZabbixWireClientOptions>>().Get("bound");
    if (boundOptions is not { Server: "127.0.0.1:9", BatchSize: 10 })
    {
        Console.WriteLine($"aot smoke: the builder bound Server '{boundOptions.Server}' and BatchSize {boundOptions.BatchSize}");
        return 1;
    }
}
// A shared client outlives a caller's dispose: the container owns it.
await registered.DisposeAsync().ConfigureAwait(false);
foreach ((string kind, ZabbixWireClient sending) in new[] { ("registered", registered), ("built", built) })
{
    try
    {
        await sending.SendAsync("smoke", new ZabbixItemKey<long>("smoke.key"), 1L, CancellationToken.None).ConfigureAwait(false);
    }
    catch (System.Net.Sockets.SocketException e)
    {
        Console.WriteLine($"{kind} sender: {e.GetType().Name}");
    }
}

// The same without a container: a client built from the frontend's address, and an agent constructed directly.
await using (var direct = new ZabbixClient("http://127.0.0.1:9/", ZabbixCredential.Anonymous))
{
    try
    {
        Console.WriteLine($"api version: {await direct.GetApiVersionAsync().ConfigureAwait(false)}");
    }
    catch (HttpRequestException e)
    {
        Console.WriteLine($"direct api: {e.GetType().Name}");
    }
}
// The agents' builders, each over a container of its own: the generic verbs, a service the container builds for a poll
// and an item, the trapper the push agent starts after itself, and the items answered through the agent's table.
ZabbixItemKey<long> pending = new("smoke.pending");
ZabbixPushAgentBuilder pushing = ZabbixPushAgent.CreateBuilder("smoke")
    .AddServer("127.0.0.1:9")
    .AddTrapper(10098, o => o.AllowedPeers.Add("127.0.0.1"))
    .AddPoll(pending, TimeSpan.FromMinutes(1), (Counter counter) => counter.Value);
pushing.Services.AddSingleton(new Counter(5));
await using (ZabbixPushAgent relay = pushing.Build())
{
    await relay.StartAsync().ConfigureAwait(false);
    Console.WriteLine("push agent: started");
}

ZabbixActiveAgentBuilder answering = ZabbixActiveAgent.CreateBuilder("smoke")
    .AddServer("127.0.0.1:9")
    .AddItem(pending, (Counter counter) => counter.Value)
    .AddItem(new ZabbixItemKey<long>("smoke.length"), key => key[0].Length);
answering.Services.AddSingleton(new Counter(7));
await using (ZabbixActiveAgent active = answering.Build())
{
    string answered = (await active.Items.AnswerAsync(ZabbixItemKey.Parse("smoke.pending"), TimeSpan.FromSeconds(5)).ConfigureAwait(false)).Value ?? "";
    string length = (await active.Items.AnswerAsync(ZabbixItemKey.Parse("smoke.length[abc]"), TimeSpan.FromSeconds(5)).ConfigureAwait(false)).Value ?? "";
    if ((answered, length) != ("7", "3"))
    {
        Console.WriteLine($"aot smoke: the active agent answered '{answered}' and '{length}'");
        return 1;
    }
    Console.WriteLine("active agent: answered");
}

await using (ZabbixPassiveAgent passive = ZabbixPassiveAgent.CreateBuilder("smoke")
    .AllowPeer(System.Net.IPAddress.Loopback)
    .Configure(o => { o.Address = "127.0.0.1"; o.Port = 0; })
    .AddItem(pending, () => 9L)
    .Build())
{
    await passive.StartAsync().ConfigureAwait(false);
    using var getter = new ZabbixWireClient();
    long polled = await getter.GetAgentItemAsync($"127.0.0.1:{((System.Net.IPEndPoint)passive.Listener!.LocalEndPoint!).Port}", pending, CancellationToken.None).ConfigureAwait(false);
    if (polled != 9)
    {
        Console.WriteLine($"aot smoke: the passive agent answered {polled}");
        return 1;
    }
    Console.WriteLine("passive agent: answered");
}

// A listener built on Kestrel's socket transport, a trapper answering it, and a value the wire client sends there.
Channel<ZabbixValue> trapped = Channel.CreateBounded<ZabbixValue>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.Wait });
await using (ZabbixListener listener = ZabbixListener.CreateBuilder()
    .UseSocketTransport(new IPEndPoint(IPAddress.Loopback, 0))
    .AllowPeer(IPAddress.Loopback)
    .OnRequest(new ZabbixTrapper(trapped.Writer).Handler)
    .Build())
{
    await listener.StartingAsync().ConfigureAwait(false);
    await listener.StartAsync().ConfigureAwait(false);
    using var sending = new ZabbixWireClient($"127.0.0.1:{((IPEndPoint)listener.LocalEndPoint!).Port}");
    await sending.SendAsync("smoke", pending, 3L, CancellationToken.None).ConfigureAwait(false);
    if (!trapped.Reader.TryRead(out ZabbixValue? value) || value.Value != "3")
    {
        Console.WriteLine("aot smoke: the listener's trapper took nothing");
        return 1;
    }
    Console.WriteLine("listener: trapped");
}

// The proxies' builders: an active proxy whose server isn't there, which starts on the configuration it holds, and a
// passive proxy whose zabbix.stats answer is the builder's OnStats.
await using (ZabbixActiveProxy active = ZabbixActiveProxy.CreateBuilder("smoke")
    .WithServer("127.0.0.1:9")
    .AddTrapper(0, o => o.Address = "127.0.0.1")
    .AllowPeer(IPAddress.Loopback)
    .UseMemoryStore()
    .Build())
{
    await active.StartAsync().ConfigureAwait(false);
    Console.WriteLine($"active proxy: started, holding configuration revision {active.Config.ConfigRevision}");
}

JsonElement one = JsonDocument.Parse("1").RootElement.Clone();
await using (ZabbixPassiveProxy passive = ZabbixPassiveProxy.CreateBuilder("smoke")
    .WithVersion("7.4")
    .AddTrapper(0, o => o.Address = "127.0.0.1")
    .AllowPeer(IPAddress.Loopback)
    .OnStats((_, _) => ValueTask.FromResult<IReadOnlyDictionary<string, JsonElement>?>(new Dictionary<string, JsonElement> { ["smoke"] = one }))
    .Build())
{
    await passive.StartAsync().ConfigureAwait(false);
    string stats = await AskAsync((IPEndPoint)passive.Listener!.LocalEndPoint!, """{"request":"zabbix.stats"}""").ConfigureAwait(false);
    if (!stats.Contains("\"smoke\":1", StringComparison.Ordinal))
    {
        Console.WriteLine($"aot smoke: the passive proxy answered {stats}");
        return 1;
    }
    Console.WriteLine("passive proxy: answered");
}

// The bundled keys, answered with nothing else composed. A handler's failure comes back as the reason a key is not
// supported, so a member the trimmer dropped or a native call that does not bind would pass silently without this: every
// key here is one the platform this runs on answers, and a refusal fails the run.
ZabbixItemDictionary bundled = ZabbixItemDictionary.BuiltIn("smoke");
string file = Environment.ProcessPath ?? throw new InvalidOperationException("The probe has no path.");
string[] keys =
[
    "agent.ping",
    "agent.variant",
    "system.hostname",
    "system.localtime",
    "system.cpu.num",
    "system.cpu.discovery",
    "system.sw.arch",
    "system.hw.macaddr",
    "proc.num",
    "vfs.fs.discovery",
    "vfs.fs.get",
    $"vfs.file.exists[{file}]",
    $"vfs.file.size[{file}]",
    $"vfs.file.md5sum[{file}]",
    $"vfs.file.cksum[{file}]",
    $"vfs.dir.count[{Path.GetDirectoryName(file)}]",
    "net.if.discovery",
    "net.tcp.listen[10099]",
    "net.tcp.port[127.0.0.1,9]",
    "net.dns[,localhost,A]",
    $"vfs.file.time[{file},change]",
    $"vfs.file.owner[{file}]",
    $"vfs.file.get[{file}]",
    $"vfs.dir.get[{Path.GetDirectoryName(file)}]",
    $"vfs.dir.size[{Path.GetDirectoryName(file)},,,disk]",
    .. OperatingSystem.IsWindows()
        ? (string[])["vm.memory.size", "vm.memory.size[cached]", "vm.vmemory.size", "system.swap.size", "system.cpu.util", $"proc_info[{Path.GetFileName(file)},wkset]", $"proc_info[{Path.GetFileName(file)},io_read_b]", $"proc_info[{Path.GetFileName(file)},gdiobj]"]
        : ["vfs.fs.inode[/]", "system.uptime", "system.boottime", "system.cpu.load", "system.cpu.util", "system.swap.size", "kernel.maxfiles", "kernel.maxproc", "vm.memory.size", "vm.memory.size[used]"],
];
int refused = 0;
foreach (string text in keys)
{
    ZabbixAgentAnswer answer = await bundled.AnswerAsync(ZabbixItemKey.Parse(text), TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
    if (answer.IsSupported) continue;
    refused++;
    Console.WriteLine($"{text}: {answer.Error}");
}
if (refused != 0)
{
    Console.WriteLine($"aot smoke: {refused} of {keys.Length} bundled keys refused");
    return 1;
}
Console.WriteLine($"bundled: {keys.Length} keys answered");

// The wire's replies read into their models by the wire's own JSON context: a server's status and statistics, an item's
// test, a report's test and a webhook's test, over an exchange that answers as a 7.4 server does.
Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> serverAnswers = (request, _) =>
{
    using JsonDocument frame = JsonDocument.Parse(request);
    string reply = frame.RootElement.GetProperty("request").GetString() switch
    {
        "status.get" => """{"response":"success","data":{"template stats":[{"count":312}],"host stats":[{"attributes":{"proxyid":0,"status":0},"count":5},{"attributes":{"proxyid":0,"status":1},"count":1},{"attributes":{"proxyid":12,"status":0},"count":2}],"required performance":[{"attributes":{"proxyid":0},"count":"4.250000"}],"server stats":{"version":"7.4.15"}}}""",
        "zabbix.stats" => """{"response":"success","data":{"boottime":1700000000,"uptime":3600,"version":"7.4.15","wcache":{"values":{"all":10,"float":4}},"process":{"trapper":{"busy":{"avg":1.5,"max":3,"min":0},"idle":{"avg":98.5,"max":100,"min":97},"count":5}},"ha":[]}}""",
        "item.test" => """{"response":"success","data":{"item":{"result":"1","eol":"LF"},"preprocessing":{"steps":[{"result":"1"}],"result":"1"}}}""",
        "report.test" => """{"response":"success","data":{"recipients":[{"status":1,"recipient":"Admin"}]}}""",
        _ => """{"response":"success","data":"ok","debug":{"logs":[{"level":4,"ms":3,"message":"sent"}],"ms":12}}""",
    };
    return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(reply));
};
await using (var asking = new ZabbixWireClient(serverAnswers))
{
    ZabbixServerStatus status = await asking.GetStatusAsync().ConfigureAwait(false);
    ZabbixServerStatistics statistics = await asking.GetStatisticsAsync().ConfigureAwait(false);
    ZabbixItemTestResult tested = await asking.TestItemAsync(new ZabbixItemTestRequest(new ZabbixItemTestItem { Key = ZabbixItemKey.Parse("agent.ping"), ValueType = 3 })).ConfigureAwait(false);
    ZabbixReportTestResult report = await asking.TestReportAsync(new ZabbixReportTestRequest("smoke", 1, 1)).ConfigureAwait(false);
    ZabbixAlertSendResult alert = await asking.SendAlertAsync(1, "ops", "s", "m").ConfigureAwait(false);
    if (status is not { TemplateCount: 312, EnabledHostCount: 5, DisabledHostCount: 1, RequiredPerformance: 4.25, Proxies: [{ ProxyId: 12, EnabledHostCount: 2 }] }
        || status.Version != new Version(7, 4, 15)
        || statistics.Uptime != TimeSpan.FromHours(1) || statistics.Processes?["trapper"].Count != 5 || statistics.WriteCache?.Values?.FloatCount != 4
        || ((IZabbixAdditionalFields)statistics).AdditionalFields?.ContainsKey("ha") != true
        || tested.Preprocessing?.Result != "1" || report.Recipients is not [{ Status: 1 }]
        || alert.Log?.Elapsed != TimeSpan.FromMilliseconds(12) || alert.Value != "ok")
    {
        Console.WriteLine("aot smoke: a server wire reply didn't read into its model");
        return 1;
    }
}

// The API's replies the generated records read: a comparison with an empty one for [], a script's result, a token, and the
// frontend link of a record with a page.
ZabbixConfigurationComparison comparison = JsonSerializer.Deserialize("""{"templates":{"updated":[{"before":{"name":"a"},"after":{"name":"b"},"items":{"added":[{"after":{"key":"k"}}]}}]}}""", TypeInfo<ZabbixConfigurationComparison>())!;
ZabbixScriptExecuteResult executed = JsonSerializer.Deserialize("""{"response":"success","value":"pong","debug":{"logs":[{"level":3,"ms":1,"message":"m"}],"ms":2}}""", TypeInfo<ZabbixScriptExecuteResult>())!;
ZabbixSliReport sli = JsonSerializer.Deserialize("""{"periods":[{"period_from":1700000000,"period_to":1700086400}],"serviceids":["5"],"sli":[[{"uptime":86000,"downtime":400,"sli":99.5,"error_budget":-100,"excluded_downtimes":[]}]]}""", TypeInfo<ZabbixSliReport>())!;
Uri hostPage = new ZabbixFrontend(new Uri("https://zbx.example/"), new Version(7, 4)).UriFor(new ZabbixHost { HostId = 10084 });
if (comparison.Templates?.Updated?[0].Items?.Added?.Count != 1 || executed.Debug?.Elapsed != TimeSpan.FromMilliseconds(2)
    || sli.Sli?[0][0].Downtime != TimeSpan.FromSeconds(400) || sli.Sli[0][0].ErrorBudget != TimeSpan.FromSeconds(-100)
    || hostPage.AbsoluteUri != "https://zbx.example/zabbix.php?action=popup&popup=host.edit&hostid=10084")
{
    Console.WriteLine("aot smoke: an API reply didn't read into its record, or a record's page didn't link");
    return 1;
}

// The SDK's redactors, set on the application's redaction builder and activated by the container.
var redacting = new ServiceCollection();
redacting.AddRedaction(redaction => redaction.SetZabbixRedactors());
await using (ServiceProvider redactors = redacting.BuildServiceProvider())
{
    Microsoft.Extensions.Compliance.Redaction.IRedactorProvider provider2 = redactors.GetRequiredService<Microsoft.Extensions.Compliance.Redaction.IRedactorProvider>();
    if (provider2.GetRedactor(ZabbixDataClassifications.Secret).Redact("secret").Length != 0
        || provider2.GetRedactor(ZabbixDataClassifications.ItemKey).Redact("web.page.get[a,b]") != "web.page.get[***]")
    {
        Console.WriteLine("aot smoke: the SDK's redactors didn't redact");
        return 1;
    }
}

Console.WriteLine("aot smoke: ok");
return 0;

// The generated JSON context's contract for one of its types, as ZabbixJson.Options resolves it.
static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)ZabbixJson.Options.GetTypeInfo(typeof(T));

// One request in Zabbix's framing (ZBXD, flags 1, a little-endian length and four reserved bytes), and the one reply.
static async Task<string> AskAsync(IPEndPoint endPoint, string request)
{
    using var client = new TcpClient();
    await client.ConnectAsync(endPoint).ConfigureAwait(false);
    NetworkStream stream = client.GetStream();
    byte[] payload = Encoding.UTF8.GetBytes(request);
    byte[] frame = new byte[13 + payload.Length];
    "ZBXD\u0001"u8.CopyTo(frame);
    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(5), payload.Length);
    payload.CopyTo(frame, 13);
    await stream.WriteAsync(frame).ConfigureAwait(false);
    byte[] header = new byte[13];
    await stream.ReadExactlyAsync(header).ConfigureAwait(false);
    byte[] reply = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(5))];
    await stream.ReadExactlyAsync(reply).ConfigureAwait(false);
    return Encoding.UTF8.GetString(reply);
}

/// <summary>A service a poll and an item read from the agent's container.</summary>
/// <param name="value">The count.</param>
internal sealed class Counter(long value)
{
    public long Value => value;
}
