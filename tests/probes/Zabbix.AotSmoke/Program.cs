// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
services.AddZabbix("smoke", o => o.Endpoint = new Uri("http://127.0.0.1:9/api_jsonrpc.php"));
services.AddZabbixPushAgent("relay").AddTrapper(10099, o => o.AllowedPeers.Add("127.0.0.1")).AddServer("127.0.0.1:9");
services.AddZabbixWireClient("smoke").AddServer("127.0.0.1:9").WithTimeout(TimeSpan.FromSeconds(5));
await using ServiceProvider provider = services.BuildServiceProvider();
ZabbixClient client = provider.GetRequiredService<IZabbixClientFactory>().Create("smoke");
try
{
    var hosts = await client.Hosts.GetAsync(new ZabbixHostGetParameters { Output = ZabbixOutput.Of("hostid", "name") }).ConfigureAwait(false);
    Console.WriteLine($"hosts: {hosts.Count}");
}
catch (HttpRequestException e)
{
    Console.WriteLine($"api: {e.GetType().Name}");
}

using var wire = new ZabbixWireClient("127.0.0.1:9");
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
using ZabbixWireClient built = ZabbixWireClient.CreateBuilder().Bind(wireSection).WithTimeout(TimeSpan.FromSeconds(5)).Build();
if (built.Options is not { Server: "127.0.0.1:9", BatchSize: 10 })
{
    Console.WriteLine($"aot smoke: the builder bound Server '{built.Options.Server}' and BatchSize {built.Options.BatchSize}");
    return 1;
}
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
    string answered = (await active.Items.AnswerAsync(ZabbixItemKey.Parse("smoke.pending", null), TimeSpan.FromSeconds(5)).ConfigureAwait(false)).Value ?? "";
    string length = (await active.Items.AnswerAsync(ZabbixItemKey.Parse("smoke.length[abc]", null), TimeSpan.FromSeconds(5)).ConfigureAwait(false)).Value ?? "";
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
    .AddServer("127.0.0.1:9")
    .AddTrapper(0, o => o.Address = "127.0.0.1")
    .AllowPeer(IPAddress.Loopback)
    .UseMemoryStore()
    .Build())
{
    await active.StartAsync().ConfigureAwait(false);
    Console.WriteLine($"active proxy: started, its trapper on {active.Trapper!.LocalEndPoint}");
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
    ZabbixAgentAnswer answer = await bundled.AnswerAsync(ZabbixItemKey.Parse(text, null), TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
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

Console.WriteLine("aot smoke: ok");
return 0;

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
