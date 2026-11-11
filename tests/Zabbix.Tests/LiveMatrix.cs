// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;

namespace Community.Zabbix.Tests;

/// <summary>
/// The live matrix's one environment contract, read once. Every live test takes its servers from here, so one
/// set of variables (printed by <c>tests/lab/lab.sh env</c>) runs the whole matrix against every version:
/// <list type="bullet">
/// <item><c>ZABBIX_TEST_APIS</c> — <c>label=url|token;…</c>, e.g.
/// <c>7.0=http://localhost:18070/api_jsonrpc.php|@/tmp/zbxlab/token-18070</c>. A token starting with <c>@</c> is
/// read from that file (so the secret never sits in the environment or a CI log); a URL without a <c>.php</c> path gets
/// <c>/api_jsonrpc.php</c>. An entry without <c>label=</c> (the bare <c>url|token</c> form) is labelled by the lab's port for
/// that version (18070, 18074, 18080), else by its authority.</item>
/// <item><c>ZABBIX_TEST_TRAPPERS</c> — <c>label=host:port;…</c>: each server's trapper.</item>
/// <item><c>ZABBIX_TEST_FEATURES</c> — <c>label=cert,connector;…</c>: what a server was started with beyond the image
/// defaults (the lab's certificate files, <c>StartConnectors</c>).</item>
/// <item><c>ZABBIX_TEST_PKI</c> — the lab CA's folder (<c>ca.crt</c>, <c>client.crt</c>, <c>client.key</c>).</item>
/// <item><c>ZABBIX_TEST_HOST_FROM_SERVER</c> — the name a server uses to reach a listener on this host
/// (<c>host.containers.internal</c> under rootless podman).</item>
/// <item><c>ZABBIX_TEST_POSTGRES</c> — the EF store's own database (read by <see cref="Lab.Postgres"/>).</item>
/// </list>
/// Unset is a skip, per version, with the remedy — never a failure and never a silent pass.
/// </summary>
internal static class LiveMatrix
{
    /// <summary>Every object a live test creates starts with this, and the sweep deletes by it.</summary>
    public const string Prefix = "zbx-it-";

    /// <summary>The versions every live theory runs against, whether configured or not: an unconfigured one is a skip.</summary>
    public static TheoryData<string> Versions => new("7.0", "7.4", "8.0");

    private static readonly Lazy<IReadOnlyList<LiveTarget>> Parsed = new(Parse);

    /// <summary>The configured servers.</summary>
    public static IReadOnlyList<LiveTarget> Servers => Parsed.Value;

    /// <summary>The lab CA's folder, when the matrix was given one.</summary>
    public static string? Pki { get; } = Environment.GetEnvironmentVariable("ZABBIX_TEST_PKI") is { Length: > 0 } pki ? pki : null;

    /// <summary>How a server inside its pod reaches a listener on this host.</summary>
    public static string HostFromServer { get; } =
        Environment.GetEnvironmentVariable("ZABBIX_TEST_HOST_FROM_SERVER") is { Length: > 0 } host ? host : "host.containers.internal";

    /// <summary>The server labelled <paramref name="label"/>, or a skip that says how to configure it.</summary>
    public static LiveTarget Require(string label)
    {
        LiveTarget? target = Servers.FirstOrDefault(s => s.Label == label);
        if (target is null)
            Assert.Skip($"ZABBIX_TEST_APIS has no {label} server; start the lab (tests/lab/lab.sh start) and eval \"$(tests/lab/lab.sh env)\"");
        return target;
    }

    /// <summary>The server labelled <paramref name="label"/> with its trapper, or a skip.</summary>
    public static (LiveTarget Target, ZabbixServerNode Trapper) RequireTrapper(string label)
    {
        LiveTarget target = Require(label);
        if (target.Trapper is null) Assert.Skip($"ZABBIX_TEST_TRAPPERS has no {label} trapper");
        return (target, target.Trapper.Value);
    }

    /// <summary>A name no other run uses: <see cref="Prefix"/>, what it is, and eight random hex digits.</summary>
    public static string Name(string what) => Prefix + what + "-" + Guid.NewGuid().ToString("N")[..8];

    private static List<LiveTarget> Parse()
    {
        Dictionary<string, ZabbixServerNode> trappers = Pairs("ZABBIX_TEST_TRAPPERS").ToDictionary(p => p.Key, p => ZabbixServerNode.Parse(p.Value, CultureInfo.InvariantCulture), StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> features = Pairs("ZABBIX_TEST_FEATURES").ToDictionary(
            p => p.Key,
            p => p.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.Ordinal);
        var servers = new List<LiveTarget>();
        foreach (string entry in Entries("ZABBIX_TEST_APIS"))
        {
            (string? label, string rest) = SplitLabel(entry);
            string[] parts = rest.Split('|', 2);
            if (parts.Length != 2) throw new FormatException($"ZABBIX_TEST_APIS entry '{entry}' is not [label=]url|token");
            var endpoint = new Uri(parts[0].EndsWith(".php", StringComparison.Ordinal) ? parts[0] : parts[0].TrimEnd('/') + "/api_jsonrpc.php");
            string token = parts[1].StartsWith('@') ? File.ReadAllText(parts[1][1..]).Trim() : parts[1];
            label ??= endpoint.Port switch { 18070 => "7.0", 18074 => "7.4", 18080 => "8.0", _ => endpoint.Authority };
            servers.Add(new LiveTarget(
                label, endpoint, token,
                trappers.TryGetValue(label, out ZabbixServerNode trapper) ? (ZabbixServerNode?)trapper : null,
                features.TryGetValue(label, out HashSet<string>? has) ? has : []));
        }
        return servers;
    }

    private static IEnumerable<string> Entries(string variable)
        => (Environment.GetEnvironmentVariable(variable) ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<KeyValuePair<string, string>> Pairs(string variable)
    {
        foreach (string entry in Entries(variable))
        {
            (string? label, string rest) = SplitLabel(entry);
            if (label is not null) yield return new(label, rest);
        }
    }

    // "7.0=http://…" is labelled; "http://…" is not (its first '=' is after "://", or there is none).
    private static (string? Label, string Value) SplitLabel(string entry)
    {
        int equals = entry.IndexOf('=', StringComparison.Ordinal);
        int scheme = entry.IndexOf("://", StringComparison.Ordinal);
        return equals > 0 && (scheme < 0 || equals < scheme) ? (entry[..equals].Trim(), entry[(equals + 1)..].Trim()) : (null, entry);
    }
}

/// <summary>One server of the live matrix.</summary>
/// <param name="Label">Its major.minor, as the matrix names it (<c>7.0</c>, <c>7.4</c>, <c>8.0</c>).</param>
/// <param name="Endpoint">The JSON-RPC endpoint.</param>
/// <param name="Token">An API token of a Super admin.</param>
/// <param name="Trapper">The trapper, when configured.</param>
/// <param name="Features">What it was started with beyond the image defaults.</param>
internal sealed record LiveTarget(string Label, Uri Endpoint, string Token, ZabbixServerNode? Trapper, IReadOnlySet<string> Features)
{
    /// <summary>A client of this server.</summary>
    public ZabbixClient Connect() => new(Endpoint, new ZabbixApiTokenCredential(Token));

    /// <summary>Whether it was started with <paramref name="feature"/>; a skip naming the remedy when not.</summary>
    public void RequireFeature(string feature, string why)
    {
        if (!Features.Contains(feature))
            Assert.Skip($"the {Label} server was not started with '{feature}' ({why}); recreate it from tests/lab/zabbix-{Label}.yaml");
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Label} ({Endpoint.Authority})";
}

/// <summary>
/// One live scenario's objects: a connection, the names it may create (all under one run-unique prefix), what to
/// delete when it ends (last created, first deleted), and the check that nothing under that prefix survived — the leak
/// sweep finds zero on every type after every scenario, or the scenario fails.
/// </summary>
internal sealed class LiveScope : IAsyncDisposable
{
    private readonly List<Func<Task>> _cleanup = [];

    private LiveScope(LiveTarget target, string run)
    {
        Target = target;
        Run = run;
        Server = target.Connect();
    }

    /// <summary>Opens a scope on the server labelled <paramref name="label"/>, or skips.</summary>
    public static LiveScope Open(string label, string scenario) => new(LiveMatrix.Require(label), LiveMatrix.Name(scenario));

    /// <summary>The server.</summary>
    public LiveTarget Target { get; }

    /// <summary>The client.</summary>
    public ZabbixClient Server { get; }

    /// <summary>This scenario's prefix: every name it creates starts with it.</summary>
    public string Run { get; }

    /// <summary>The server's API version.</summary>
    public Task<Version> VersionAsync() => Server.GetApiVersionAsync(TestContext.Current.CancellationToken);

    /// <summary>A name under this scenario's prefix.</summary>
    public string Name(string what) => Run + "-" + what;

    /// <summary>Deletes <paramref name="ids"/> with <c>{type}.delete</c> when the scope ends.</summary>
    public void Delete(string type, params IReadOnlyList<string> ids)
        => _cleanup.Add(async () => await Server.RpcAsync(type + ".delete", ids).ConfigureAwait(false));

    /// <summary>Deletes <paramref name="ids"/> with <c>{type}.delete</c> when the scope ends.</summary>
    public void Delete(string type, IReadOnlyList<ulong> ids) => Delete(type, [.. ids.Select(id => id.ToString(CultureInfo.InvariantCulture))]);

    /// <summary>Runs <paramref name="cleanup"/> when the scope ends.</summary>
    public void Defer(Func<Task> cleanup) => _cleanup.Add(cleanup);

    /// <summary>Deletes what was recorded, then asserts nothing under <see cref="Run"/> is left on any type.</summary>
    public async ValueTask DisposeAsync()
    {
        var failures = new List<string>();
        for (int i = _cleanup.Count - 1; i >= 0; i--)
        {
            try { await _cleanup[i]().ConfigureAwait(false); }
            catch (ZabbixApiException failure) { failures.Add(failure.Message); }
        }
        IReadOnlyList<string> leaks = await LiveSweep.RunAsync(Server, Run, delete: true).ConfigureAwait(false);
        Server.Dispose();
        Assert.True(leaks.Count == 0, $"the scenario leaked {leaks.Count} object(s) (now swept): {string.Join("; ", leaks)}; cleanup errors: {string.Join("; ", failures)}");
    }
}

/// <summary>
/// The C# twin of <c>Invoke-LabLeakSweep</c> (tests/lab/Lab.psm1): every type, searched by name for a prefix with <c>search</c> and without
/// <c>searchWildcardsEnabled</c> (a substring match), each hit re-checked for the prefix, dependents first.
/// </summary>
internal static class LiveSweep
{
    // Dependents before the objects they hang off: (type, id field, name field).
    private static readonly (string Type, string Id, string Name)[] Types =
    [
        ("action", "actionid", "name"), ("correlation", "correlationid", "name"), ("service", "serviceid", "name"),
        ("sla", "slaid", "name"), ("maintenance", "maintenanceid", "name"), ("drule", "druleid", "name"),
        ("report", "reportid", "name"), ("dashboard", "dashboardid", "name"), ("map", "sysmapid", "name"),
        ("token", "tokenid", "name"), ("httptest", "httptestid", "name"), ("trigger", "triggerid", "description"),
        ("graph", "graphid", "name"), ("item", "itemid", "name"), ("valuemap", "valuemapid", "name"),
        ("host", "hostid", "host"), ("template", "templateid", "host"), ("hostgroup", "groupid", "name"),
        ("templategroup", "groupid", "name"), ("user", "userid", "username"), ("usergroup", "usrgrpid", "name"),
        ("role", "roleid", "name"), ("mediatype", "mediatypeid", "name"), ("script", "scriptid", "name"),
        ("proxy", "proxyid", "name"), ("proxygroup", "proxy_groupid", "name"), ("connector", "connectorid", "name"),
        ("iconmap", "iconmapid", "name"), ("image", "imageid", "name"), ("regexp", "regexpid", "name"),
        ("userdirectory", "userdirectoryid", "name"), ("mfa", "mfaid", "name"), ("usermacro", "globalmacroid", "macro"),
    ];

    /// <summary>Every object whose name starts with <paramref name="prefix"/>, as <c>type id name</c>; deleted when asked.</summary>
    public static async Task<IReadOnlyList<string>> RunAsync(ZabbixClient client, string prefix, bool delete)
    {
        var found = new List<string>();
        string macroPrefix = "{$" + prefix.ToUpperInvariant().Replace('-', '_');
        foreach ((string type, string idField, string nameField) in Types)
        {
            JsonElement hits;
            try
            {
                hits = type == "usermacro"
                    ? await client.RpcAsync("usermacro.get", new Dictionary<string, object> { ["output"] = new[] { idField, nameField }, ["globalmacro"] = true, ["search"] = new Dictionary<string, string> { [nameField] = macroPrefix } }).ConfigureAwait(false)
                    : await client.RpcAsync(type + ".get", new Dictionary<string, object> { ["output"] = new[] { idField, nameField }, ["search"] = new Dictionary<string, string> { [nameField] = prefix } }).ConfigureAwait(false);
            }
            catch (ZabbixApiException refused) when (refused.Kind is ZabbixApiErrorKind.MethodNotFound or ZabbixApiErrorKind.Validation)
            {
                continue;                                                                   // the version has no such method
            }
            foreach (JsonElement hit in hits.EnumerateArray())
            {
                string name = hit.GetProperty(nameField).GetString() ?? string.Empty;
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !name.StartsWith(macroPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string id = hit.GetProperty(idField).GetString()!;
                found.Add($"{type} {id} {name}");
                if (!delete) continue;
                try { await client.RpcAsync(type == "usermacro" ? "usermacro.deleteglobal" : type + ".delete", new[] { id }).ConfigureAwait(false); }
                catch (ZabbixApiException) { }                                              // deleted with its parent already
            }
        }
        return found;
    }
}

/// <summary>The raw JSON-RPC door and the observable wait every live test uses.</summary>
internal static class LiveApi
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.General);

    /// <summary>Calls <paramref name="method"/> with <paramref name="parameters"/> serialised as they are, and returns the raw result.</summary>
    public static async Task<JsonElement> RpcAsync(this ZabbixClient client, string method, object parameters)
    {
        JsonElement body = parameters as JsonElement? ?? JsonSerializer.SerializeToElement(parameters, parameters.GetType(), Web);
        return await client.Transport.SendAsync(method, body, TestJson.Default.JsonElement, TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>Calls <paramref name="method"/> with a JSON literal.</summary>
    public static Task<JsonElement> RpcJsonAsync(this ZabbixClient client, string method, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return client.RpcAsync(method, document.RootElement.Clone());
    }

    /// <summary>
    /// Polls <paramref name="probe"/> until it returns a value, for at most <paramref name="budget"/> (60 s by default):
    /// the wait is on the server's observable state (a value in <c>history.get</c>, a host's availability), never a
    /// fixed sleep; the budget only bounds a server that never gets there, and the failure says what was awaited.
    /// </summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T?>> probe, string awaited, TimeSpan? budget = null)
    {
        TimeProvider time = TimeProvider.System;
        long start = time.GetTimestamp();
        TimeSpan limit = budget ?? TimeSpan.FromSeconds(60);
        while (true)
        {
            if (await probe().ConfigureAwait(false) is { } value) return value;
            if (time.GetElapsedTime(start) > limit) Assert.Fail($"waited {limit.TotalSeconds:0} s for {awaited}");
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The first element's <paramref name="property"/> as a string.</summary>
    public static string Id(this JsonElement result, string property) => result.GetProperty(property)[0].GetString()!;

    /// <summary>The first id of <paramref name="property"/> in a write's result, parsed.</summary>
    public static ulong IdOf(this JsonElement result, string property) => ulong.Parse(result.Id(property), CultureInfo.InvariantCulture);
}
