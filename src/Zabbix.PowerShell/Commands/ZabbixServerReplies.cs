// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// The shape a server-wire <c>Test-</c> writes when the server's reply carries no <c>data</c>: the reply's own
/// <c>response</c> and <c>info</c>, as the same <c>[pscustomobject]</c> a reply with data becomes, so the command writes
/// one shape and never <see langword="null"/>.
/// </summary>
internal static class ZabbixServerReplies
{
    /// <summary>The reply's <c>response</c> and <c>info</c> as a JSON object.</summary>
    /// <param name="reply">The server's reply.</param>
    public static JsonElement Envelope(ZabbixServerReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var envelope = new JsonObject { ["response"] = reply.Response, ["info"] = reply.Info };
        using JsonDocument document = JsonDocument.Parse(envelope.ToJsonString());
        return document.RootElement.Clone();
    }
}
