// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Community.Zabbix.Tests;

/// <summary>A wire double: captures the outgoing request and body and answers a canned JSON-RPC reply, echoing the request id when asked.</summary>
internal sealed class CapturingHandler(string responseJson, bool echoId = false) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastBody { get; private set; }

    public List<long> Ids { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        string reply = responseJson;
        if (request.Content is not null)
        {
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument body = JsonDocument.Parse(LastBody);
            long id = body.RootElement.GetProperty("id").GetInt64();
            Ids.Add(id);
            if (echoId) reply = reply.Replace("\"id\":1", "\"id\":" + id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
    }
}
