// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Text.Json;

namespace Community.Zabbix;

/// <summary>
/// Reads a buffered reply far enough to tell whether it is a transient JSON-RPC <c>error</c> (-32603: the frontend could
/// not start; never a -32500, which the method raised and would raise again, <see cref="ZabbixApiErrorKind.Database"/> included),
/// which the API delivers with HTTP 200 and the standard retry predicate therefore passes as a success. Zabbix writes the
/// envelope in the order <c>jsonrpc</c>, <c>result</c>|<c>error</c>, <c>id</c> (<c>CJsonRpc.php</c> <c>processResult</c>,
/// <c>jsonError</c>), so the first member decides: a <c>result</c> is skipped over, never parsed here.
/// </summary>
internal static class ZabbixTransientReply
{
    public static bool IsTransient(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode || response.Content is not ZabbixBufferedContent buffered) return false;
        try
        {
            return Peek(buffered.Body) == Envelope.Error && Classify(JsonDocument.Parse(buffered.Body));
        }
        catch (JsonException)
        {
            return false;       // not an envelope: the transport reports it, the retry predicate does not guess
        }
    }

    private static bool Classify(JsonDocument document)
    {
        using (document)
        {
            if (!document.RootElement.TryGetProperty("error", out JsonElement error) || error.ValueKind != JsonValueKind.Object) return false;
            // Kind-checked as the transport reads it: TryGetInt32 on a code written as text throws InvalidOperationException,
            // which, inside the retry predicate, would fail the whole call with it instead of the API's refusal.
            int code = error.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int value) ? value : 0;
            string? data = error.TryGetProperty("data", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            return ZabbixApiException.Classify(code, data) == ZabbixApiErrorKind.Transient;
        }
    }

    private enum Envelope { Unknown, Result, Error }

    private static Envelope Peek(ReadOnlySequence<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return Envelope.Unknown;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("result"u8)) return Envelope.Result;
            if (reader.ValueTextEquals("error"u8)) return Envelope.Error;
            if (!reader.TrySkip()) return Envelope.Unknown;
        }
        return Envelope.Unknown;
    }
}
