// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;
using System.Text.Json;

namespace Community.Zabbix.Protocol;

/// <summary>The protocol request a frame carries, as the wire's telemetry names an exchange (<c>rpc.method</c>).</summary>
internal static class ZabbixRequestName
{
    /// <summary>The name of any payload that is not one of the protocol's requests.</summary>
    public const string OtherRequest = "_OTHER";

    private static readonly FrozenSet<string> s_requests = FrozenSet.Create(StringComparer.Ordinal,
        "sender data", "agent data", "active checks", "active check heartbeat", "passive checks", "proxy config", "proxy data",
        "history.push", "queue.get", "status.get", "zabbix.stats");

    /// <summary>
    /// The request a frame's payload names (its top-level <c>request</c>), when it is one of the protocol's requests;
    /// <c>_OTHER</c> for any other payload (a plain-text passive check, an unknown request), so the tag's values stay the
    /// protocol's few and never carry a payload's own text.
    /// </summary>
    /// <param name="payload">The frame's payload.</param>
    /// <returns>The request's name, or <c>_OTHER</c>.</returns>
    public static string Of(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload[0] != (byte)'{') return OtherRequest;
        var reader = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = 64 });
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return OtherRequest;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isRequest = reader.ValueTextEquals("request"u8);
                if (!reader.Read()) return OtherRequest;
                if (isRequest)
                    return reader.TokenType == JsonTokenType.String && reader.GetString() is { } name && s_requests.TryGetValue(name, out string? known) ? known : OtherRequest;
                if (!reader.TrySkip()) return OtherRequest;
            }
        }
        catch (JsonException)
        {
            // a payload cut short by the reader's view (a partial frame) or not JSON after all names no request
        }
        return OtherRequest;
    }
}
