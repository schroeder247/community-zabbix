// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Community.Zabbix.Tests;

/// <summary>
/// A Zabbix API double that answers by method: a script maps a method to a function of its params and returns the
/// JSON <c>result</c>; every call is recorded (method and params) so a test can assert the wire the tree compiled.
/// </summary>
internal sealed class ScriptedApi : HttpMessageHandler
{
    private readonly Dictionary<string, Func<JsonElement, string>> _script = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);

    public List<(string Method, JsonElement Params)> Calls { get; } = [];

    public ScriptedApi On(string method, Func<JsonElement, string> result)
    {
        _script[method] = result;
        return this;
    }

    public ScriptedApi On(string method, string result) => On(method, _ => result);

    /// <summary>Answers <paramref name="method"/> with an <c>error</c> object, as the server's <c>CApiService::exception</c> writes one.</summary>
    public ScriptedApi OnError(string method, int code, string message, string data)
    {
        _errors[method] = $$"""{"code":{{code.ToString(CultureInfo.InvariantCulture)}},"message":"{{message}}","data":"{{data}}"}""";
        return this;
    }

    public IEnumerable<JsonElement> ParamsOf(string method) => Calls.Where(call => call.Method == method).Select(call => call.Params);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using JsonDocument envelope = JsonDocument.Parse(body);
        string method = envelope.RootElement.GetProperty("method").GetString()!;
        long id = envelope.RootElement.GetProperty("id").GetInt64();
        JsonElement parameters = envelope.RootElement.GetProperty("params").Clone();
        lock (Calls) Calls.Add((method, parameters));

        string reply = _errors.TryGetValue(method, out string? error)
            ? $$"""{"jsonrpc":"2.0","error":{{error}},"id":{{id.ToString(CultureInfo.InvariantCulture)}}}"""
            : _script.TryGetValue(method, out Func<JsonElement, string>? answer)
            ? $$"""{"jsonrpc":"2.0","result":{{answer(parameters)}},"id":{{id.ToString(CultureInfo.InvariantCulture)}}}"""
            : $$"""{"jsonrpc":"2.0","error":{"code":-32601,"message":"Method not found","data":"{{method}}"},"id":{{id.ToString(CultureInfo.InvariantCulture)}}}""";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
    }
}
