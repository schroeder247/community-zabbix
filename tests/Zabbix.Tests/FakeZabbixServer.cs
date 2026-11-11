// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Community.Zabbix.Listening;
using Community.Zabbix.Proxy;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>
/// A Zabbix server's proxy side, shaped on its source so a test that passes here passes against the real one:
/// <list type="bullet">
/// <item><c>proxy config</c> (<c>zbx_send_proxyconfig</c>): a proxy of another major.minor gets
/// <c>{"response":"failed","info":"proxy and server major versions do not match","version":…}</c>; a proxy at the current
/// revision gets <c>{}</c>; revision 0 gets a full sync, anything else the delta of the hosts changed since;</item>
/// <item><c>proxy data</c> (<c>recv_proxy_data</c>): a 31-character-or-shorter session is refused; with uploads disabled a
/// request that carries <c>history data</c> gets <c>{"upload":"disabled","response":"failed"}</c> and one without it
/// <c>{"upload":"disabled","response":"success"}</c>; otherwise values whose id is above the session's <c>last_id</c> are
/// stored and the reply is <c>{"upload":"enabled","response":"success"}</c>. A <c>discovery data</c> row the server cannot
/// read (<c>process_discovery_data_contents</c>: no <c>clock</c>, <c>drule</c> or <c>ip</c>, or no <c>dcheck</c> on a row that
/// is not a finalized rule's) fails the whole message with <c>{"response":"failed","info":"cannot find pair with name
/// &quot;…&quot;"}</c>, <b>after</b> the message's history was stored, as the server processes history first
/// (<c>zbx_process_proxy_data</c>).</item>
/// </list>
/// Every frame is recorded; a test awaits what it needs on <see cref="Frames"/> rather than sleeping.
/// </summary>
internal sealed class FakeZabbixServer : IAsyncDisposable
{
    private readonly ZabbixListener _listener;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _lastIds = new(StringComparer.Ordinal);
    private readonly List<(int Revision, string Hosts, string Items)> _changes = [];

    public FakeZabbixServer(string version = "7.4.15")
    {
        Version = version;
        _listener = new ZabbixListener(new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0, AllowedPeers = { "127.0.0.1" } }, HandleAsync, new SocketTransportFactory(Options.Create(new SocketTransportOptions()), NullLoggerFactory.Instance));
    }

    /// <summary>The server's version, as it reports it.</summary>
    public string Version { get; }

    /// <summary>Every frame a proxy sent, in order.</summary>
    public Channel<JsonElement> Frames { get; } = Channel.CreateUnbounded<JsonElement>();

    /// <summary>Values the server stored, by id, de-duplicated by session <c>last_id</c> as the server does.</summary>
    public List<(long Id, long ItemId, string Value)> Stored { get; } = [];

    /// <summary>Every <c>discovery data</c> row the server received.</summary>
    public List<ZabbixProxyDiscoveryItem> StoredDiscovery { get; } = [];

    /// <summary>Every <c>auto registration</c> row the server received.</summary>
    public List<ZabbixProxyAutoregistrationItem> StoredAutoregistration { get; } = [];

    /// <summary>Every <c>interface availability</c> row the server received.</summary>
    public List<ZabbixProxyAvailabilityItem> StoredAvailability { get; } = [];

    /// <summary>Every <c>tasks</c> result row the server received.</summary>
    public List<ZabbixProxyTaskResult> StoredTasks { get; } = [];

    /// <summary>Whether history uploads are disabled (a full history cache, <c>zbx_hc_check_proxy</c>).</summary>
    public bool UploadDisabled { get; set; }

    /// <summary>
    /// Runs for every <c>proxy data</c> frame BEFORE its reply is composed, under the server's lock: a test that changes
    /// <see cref="UploadDisabled"/> (or any other knob) per frame does it here, so the change governs that frame's own
    /// reply. Doing it from a <see cref="Frames"/> reader instead races the reply (the frame is published after it).
    /// </summary>
    public Action<JsonElement>? OnProxyData { get; set; }

    /// <summary>Raw <c>tasks</c> array JSON (e.g. <c>[{"type":2,"clock":1,"ttl":0,"command":"echo"}]</c>) to hand back with the next history-carrying acknowledgement; consumed once.</summary>
    public string? PendingTasksJson { get; set; }

    /// <summary>A number of <c>proxy data</c> pages to refuse (a failed reply without <c>upload</c>, as a bad session gets).</summary>
    public int RefusePages { get; set; }

    /// <summary>A session whose history pages are always refused: a stopped proxy's, whose last frame can still be in flight.</summary>
    public string? RefusedSession { get; set; }

    /// <summary>
    /// Whether the server refuses every <c>proxy data</c> from the proxy, probe included (<c>recv_proxy_data</c>: a proxy it
    /// does not know or does not allow, "Zabbix server shutdown in progress").
    /// </summary>
    public bool RefuseProxy { get; set; }

    /// <summary>A number of <c>proxy data</c> exchanges to cut before replying.</summary>
    public int DropPages { get; set; }

    /// <summary>The <c>items</c> table's fields, quoted and comma-separated; every row given to <see cref="Configure"/> follows them.</summary>
    public string ItemFields { get; init; } = "\"itemid\",\"type\",\"hostid\",\"key_\",\"status\",\"trapper_hosts\",\"allow_traps\"";

    public ZabbixServerNode Node => new(((IPEndPoint)_listener.LocalEndPoint!).Address.ToString(), ((IPEndPoint)_listener.LocalEndPoint!).Port);

    /// <summary>The configuration: hosts and items rows as JSON arrays, e.g. <c>[10,"web-01",0]</c> for hostid, host, status.</summary>
    public void Configure(string hosts, string items)
    {
        lock (_gate) _changes.Add((_changes.Count + 1, hosts, items));
    }

    public async Task StartAsync()
    {
        await _listener.StartingAsync(default);
        await _listener.StartAsync(default);
    }

    /// <summary>Forgets the frames recorded so far (another proxy's).</summary>
    public void Drain()
    {
        while (Frames.Reader.TryRead(out _)) { }
    }

    /// <summary>The next frame of the given request kind, awaited.</summary>
    public async Task<JsonElement> NextAsync(string request)
    {
        await foreach (JsonElement frame in Frames.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
            if (frame.TryGetProperty("request", out JsonElement kind) && kind.GetString() == request) return frame;
        throw new InvalidOperationException("the server stopped");
    }

    private ValueTask<ReadOnlyMemory<byte>> HandleAsync(ZabbixRequest request, CancellationToken cancellationToken)
    {
        JsonElement frame = JsonDocument.Parse(request.Payload).RootElement.Clone();
        string kind = frame.GetProperty("request").GetString()!;
        string reply = kind switch
        {
            "proxy config" => Config(frame),
            "proxy data" => Data(frame),
            _ => """{"response":"failed","info":"unsupported request"}""",
        };
        Frames.Writer.TryWrite(frame);
        return ValueTask.FromResult<ReadOnlyMemory<byte>>(reply.Length == 0 ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(reply));
    }

    private string Config(JsonElement frame)
    {
        if (!ZabbixComponentVersion.TryParse(Version, System.Globalization.CultureInfo.InvariantCulture, out ZabbixComponentVersion server)) throw new InvalidOperationException($"not a version: {Version}");
        if (!ZabbixComponentVersion.TryParse(frame.GetProperty("version").GetString(), System.Globalization.CultureInfo.InvariantCulture, out ZabbixComponentVersion proxy) || !ZabbixProxyProtocol.ServesConfig(server, proxy))
            return $$"""{"response":"failed","info":"proxy and server major versions do not match","version":"{{Version}}"}""";
        long held = frame.GetProperty("config_revision").GetInt64();
        lock (_gate)
        {
            if (held == _changes.Count) return "{}";
            IEnumerable<(int Revision, string Hosts, string Items)> changes = held == 0 ? _changes : _changes.Where(change => change.Revision > held);
            string full = held == 0 ? "\"full_sync\":1," : string.Empty;
            string hosts = string.Join(",", changes.Select(change => change.Hosts).Where(rows => rows.Length > 0));
            string items = string.Join(",", changes.Select(change => change.Items).Where(rows => rows.Length > 0));
            const string HostsTable = "\"data\":{\"hosts\":{\"fields\":[\"hostid\",\"host\",\"status\"],\"data\":[";
            string itemsTable = "]},\"items\":{\"fields\":[" + ItemFields + "],\"data\":[";
            return "{" + full + HostsTable + hosts + itemsTable + items + "]}},\"config_revision\":"
                + _changes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        }
    }

    private string Data(JsonElement frame)
    {
        lock (_gate)
        {
            OnProxyData?.Invoke(frame);
            if (DropPages > 0 && frame.TryGetProperty("history data", out _))
            {
                DropPages--;
                return string.Empty;                                                   // the connection closes with no reply
            }
            string session = frame.GetProperty("session").GetString() ?? string.Empty;
            if (RefuseProxy) return """{"response":"failed","info":"proxy \"proxy-01\" not found"}""";
            if (session.Length != 32) return $$"""{"response":"failed","info":"invalid session token length {{session.Length}}"}""";
            bool history = frame.TryGetProperty("history data", out JsonElement rows);
            if (UploadDisabled) return history ? """{"upload":"disabled","response":"failed"}""" : """{"upload":"disabled","response":"success"}""";
            if (history && session == RefusedSession) return """{"response":"failed","info":"cannot process proxy data"}""";
            if (history && RefusePages > 0)
            {
                RefusePages--;
                return """{"response":"failed","info":"cannot process proxy data"}""";
            }
            if (history)
            {
                long last = _lastIds.GetValueOrDefault(session);
                foreach (JsonElement row in rows.EnumerateArray())
                {
                    long id = row.GetProperty("id").GetInt64();
                    if (id <= last) continue;                                           // process_history_data_by_keys: a duplicate
                    Stored.Add((id, row.GetProperty("itemid").GetInt64(), row.GetProperty("value").GetString()!));
                    last = id;
                }
                _lastIds[session] = last;
            }
            if (frame.TryGetProperty("discovery data", out JsonElement discovery) && Unreadable(discovery) is { } missing)
                return $$"""{"response":"failed","info":"cannot find pair with name \"{{missing}}\""}""";
            if (frame.TryGetProperty("discovery data", out discovery))
                StoredDiscovery.AddRange(discovery.Deserialize(ZabbixProxyJson.Default.IReadOnlyListZabbixProxyDiscoveryItem) ?? []);
            if (frame.TryGetProperty("auto registration", out JsonElement autoreg))
                StoredAutoregistration.AddRange(autoreg.Deserialize(ZabbixProxyJson.Default.IReadOnlyListZabbixProxyAutoregistrationItem) ?? []);
            if (frame.TryGetProperty("interface availability", out JsonElement availability))
                StoredAvailability.AddRange(availability.Deserialize(ZabbixProxyJson.Default.IReadOnlyListZabbixProxyAvailabilityItem) ?? []);
            if (frame.TryGetProperty("tasks", out JsonElement tasks))
                StoredTasks.AddRange(tasks.Deserialize(ZabbixProxyJson.Default.IReadOnlyListZabbixProxyTaskResult) ?? []);
            if (PendingTasksJson is { } pending)
            {
                PendingTasksJson = null;
                return $$"""{"upload":"enabled","response":"success","tasks":{{pending}}}""";
            }
            return """{"upload":"enabled","response":"success"}""";
        }
    }

    /// <summary>The first tag <c>process_discovery_data_contents</c> requires and does not find, in row order; <see langword="null"/> when every row reads.</summary>
    private static string? Unreadable(JsonElement rows)
    {
        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("clock", out _)) return "clock";
            if (!row.TryGetProperty("drule", out _)) return "drule";
            if (row.TryGetProperty("status", out JsonElement status) && status.GetInt32() == 2) continue;   // DOBJECT_STATUS_FINALIZED
            if (!row.TryGetProperty("dcheck", out _)) return "dcheck";
            if (!row.TryGetProperty("ip", out _)) return "ip";
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        Frames.Writer.TryComplete();
        await _listener.StoppingAsync(default);
        await _listener.StopAsync(default);
        await _listener.StoppedAsync(default);
        await _listener.DisposeAsync();
    }
}
