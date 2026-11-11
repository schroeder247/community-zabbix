// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// A real HTTP server playing the Zabbix JSON-RPC API on a loopback port: a script answers by method, every call is
/// recorded, a method can be made to stall until the client goes away, and every request's abort is observable — so a
/// test sees a cancellation where it matters, at the server. It also takes OTLP/HTTP posts (<c>/v1/*</c>).
/// </summary>
internal sealed class FakeZabbix : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, Func<JsonElement, string>> _script = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _stalls = new(StringComparer.Ordinal);

    private FakeZabbix(WebApplication app) => _app = app;

    public Uri Endpoint { get; private set; } = null!;

    public Uri BaseAddress { get; private set; } = null!;

    public ConcurrentQueue<(string Method, JsonElement Params, string? Authorization)> Calls { get; } = new();

    public ConcurrentQueue<string> OtlpPaths { get; } = new();

    /// <summary>Every OTLP export request's signal and protobuf body, in arrival order.</summary>
    public ConcurrentQueue<(string Signal, byte[] Body)> OtlpBodies { get; } = new();

    /// <summary>Completed with the method of each request as it arrives.</summary>
    public TaskCompletionSource<string> Arrived { get; private set; } = NewSignal<string>();

    /// <summary>Completed when a request the server was still answering is aborted by the client.</summary>
    public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Starts the fake; over HTTPS with <paramref name="certificate"/> when one is given.</summary>
    /// <param name="certificate">The server certificate (a self-signed one for a certificate check).</param>
    public static async Task<FakeZabbix> StartAsync(System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        if (certificate is null) builder.WebHost.UseUrls("http://127.0.0.1:0");
        else builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        var fake = new FakeZabbix(app);                                                    // owns the app from here
        FakeZabbix? unstarted = fake;
        try
        {
            app.MapPost("/api_jsonrpc.php", fake.AnswerAsync);
            app.MapPost("/v1/{signal}", async (HttpContext context, string signal) =>
            {
                using var body = new MemoryStream();
                await context.Request.Body.CopyToAsync(body, context.RequestAborted);
                fake.OtlpBodies.Enqueue((signal, body.ToArray()));
                fake.OtlpPaths.Enqueue(signal);
                return Results.Ok();
            });
            await app.StartAsync(TestContext.Current.CancellationToken);
            string address = app.Urls.First();
            fake.BaseAddress = new Uri(address + "/");
            fake.Endpoint = new Uri(fake.BaseAddress, "api_jsonrpc.php");
            fake.On("apiinfo.version", "\"7.0.31\"");
            unstarted = null;                                                               // started: the caller owns it now
            return fake;
        }
        finally
        {
            if (unstarted is not null) await unstarted.DisposeAsync();
        }
    }

    public FakeZabbix On(string method, Func<JsonElement, string> result)
    {
        _script[method] = result;
        return this;
    }

    public FakeZabbix On(string method, string result) => On(method, _ => result);

    /// <summary>Answers every method no <see cref="On(string, string)"/> scripts (the pipeline matrix's generic server).</summary>
    public FakeZabbix OnAny(Func<string, JsonElement, string> result)
    {
        _fallback = result;
        return this;
    }

    private Func<string, JsonElement, string>? _fallback;

    /// <summary>Makes <paramref name="method"/> stall until <see cref="Release"/> (or the client aborts).</summary>
    public FakeZabbix Stall(string method)
    {
        _stalls[method] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return this;
    }

    public void Release(string method)
    {
        if (_stalls.TryRemove(method, out TaskCompletionSource? stall)) stall.TrySetResult();
    }

    public TaskCompletionSource<string> ExpectArrival() => Arrived = NewSignal<string>();

    public IEnumerable<JsonElement> ParamsOf(string method) => Calls.Where(call => call.Method == method).Select(call => call.Params);

    public static string Hosts(params (string Id, string Name)[] hosts)
        => "[" + string.Join(',', hosts.Select(host => $$"""{"hostid":"{{host.Id}}","host":"{{host.Name}}","name":"{{host.Name}}","status":"0"}""")) + "]";

    private async Task AnswerAsync(HttpContext context)
    {
        using JsonDocument envelope = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        string method = envelope.RootElement.GetProperty("method").GetString()!;
        long id = envelope.RootElement.GetProperty("id").GetInt64();
        JsonElement parameters = envelope.RootElement.TryGetProperty("params", out JsonElement given) ? given.Clone() : default;
        Calls.Enqueue((method, parameters, context.Request.Headers.Authorization.FirstOrDefault()));
        Arrived.TrySetResult(method);

        if (_stalls.TryGetValue(method, out TaskCompletionSource? stall))
        {
            // The abort is read from the token once the wait has ended, never signalled from a registration of its own:
            // a token runs its callbacks newest first, so the wait's own registration ends the wait first, this method
            // resumes on another thread, and disposing a registration of its own here could remove it before the
            // cancelling thread reached it — the request ended aborted with no signal (CancellationRegistrationProbeTests).
            await stall.Task.WaitAsync(context.RequestAborted).ContinueWith(_ => { }, TaskScheduler.Default);
            if (context.RequestAborted.IsCancellationRequested)
            {
                Aborted.TrySetResult();
                return;
            }
        }

        string idText = id.ToString(CultureInfo.InvariantCulture);
        // Connect-ZbxServer proves a credential with one user.get: answered (with no user) unless a test scripts it, or
        // answers every method itself.
        Func<JsonElement, string>? answer = _script.TryGetValue(method, out Func<JsonElement, string>? scripted) ? scripted
            : _fallback is { } fallback ? given => fallback(method, given)
            : method == "user.get" ? _ => "[]" : null;
        string reply = answer is not null
            ? answer(parameters) is var result && result.StartsWith("{\"error\"", StringComparison.Ordinal)
                ? $$"""{"jsonrpc":"2.0",{{result[1..^1]}},"id":{{idText}}}"""
                : $$"""{"jsonrpc":"2.0","result":{{result}},"id":{{idText}}}"""
            : $$"""{"jsonrpc":"2.0","error":{"code":-32601,"message":"Method not found.","data":"Incorrect API \"{{method}}\"."},"id":{{idText}}}""";
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(reply, Encoding.UTF8, context.RequestAborted);
    }

    /// <summary>A reply that is the API's error object.</summary>
    public static string Error(int code, string message, string data)
        => $$$"""{"error":{"code":{{{code.ToString(CultureInfo.InvariantCulture)}}},"message":"{{{message}}}","data":"{{{data}}}"}}""";

    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        foreach (string method in _stalls.Keys) Release(method);
        await _app.DisposeAsync();
    }
}
