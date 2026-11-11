// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;

namespace Community.Zabbix.Protocol;

/// <summary>
/// Represents a redirect reply: a server or proxy of a proxy group answering <c>{"response":"failed","redirect":{…}}</c>
/// (<c>zbx_add_redirect_response</c>, <c>src/libs/zbxcommshigh/commshigh.c</c>, 7.0+): send to <see cref="Address"/> at
/// <see cref="Revision"/>, or, with <see cref="Reset"/>, stop preferring the node that answered. Wire plumbing: the
/// exchange that follows redirects (<see cref="ZabbixNodeLink"/>) consumes it, and nothing public carries it.
/// </summary>
/// <param name="Address">Where to send instead; <see langword="null"/> for a reset.</param>
/// <param name="Revision">The redirect's revision; an older one than a redirect already followed is not followed.</param>
/// <param name="Reset">Whether this is a reset rather than a new target.</param>
internal sealed record ZabbixRedirect(ZabbixServerNode? Address, ulong Revision, bool Reset)
{
    /// <summary>
    /// Reads a reply as upstream's <c>comms_check_redirect</c> does: <c>response</c> must be <c>failed</c>, and the
    /// <c>redirect</c> object either <c>reset</c> = <c>true</c>, or an unsigned <c>revision</c> and an <c>address</c> that
    /// parses as a <c>ServerActive</c> element (<c>zbx_parse_redirect_response</c>; the port defaults to 10051). Anything
    /// else is not a redirect, and the reply is the caller's. Top-level members are scanned without materialising them.
    /// </summary>
    /// <param name="reply">The reply's payload.</param>
    /// <param name="redirect">The redirect, when it is one.</param>
    /// <returns>Whether the reply is a valid redirect.</returns>
    public static bool TryParse(ReadOnlySpan<byte> reply, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ZabbixRedirect? redirect)
    {
        redirect = null;
        if (reply.IsEmpty || reply[0] != (byte)'{') return false;
        var reader = new Utf8JsonReader(reply);
        bool failed = false;
        bool responseSeen = false;
        ZabbixRedirect? found = null;
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isResponse = reader.ValueTextEquals("response"u8);
                bool isRedirect = reader.ValueTextEquals("redirect"u8);
                if (!reader.Read()) return false;
                if (isResponse && !responseSeen && reader.TokenType == JsonTokenType.String)
                {
                    responseSeen = true;                                                    // zbx_json_value_by_name: the first one
                    failed = reader.ValueTextEquals("failed"u8);
                }
                else if (isRedirect && found is null && reader.TokenType == JsonTokenType.StartObject)
                {
                    found = ReadBlock(ref reader);
                }
                else reader.Skip();
            }
        }
        catch (JsonException)
        {
            return false;                                                                   // not JSON: not a redirect
        }
        redirect = failed ? found : null;
        return redirect is not null;
    }

    // The redirect object: reset wins; otherwise revision and address are both required.
    private static ZabbixRedirect? ReadBlock(ref Utf8JsonReader reader)
    {
        bool reset = false;
        ulong? revision = null;
        string? address = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string name = reader.GetString() ?? string.Empty;
            reader.Read();
            switch (name)
            {
                case "reset":
                    reset = reader.TokenType == JsonTokenType.True
                            || (reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("true"u8));
                    break;
                case "revision":
                    revision = Unsigned(ref reader);
                    break;
                case "address":
                    address = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        if (reset) return new ZabbixRedirect(null, 0, Reset: true);
        if (revision is not { } number || address is null) return null;
        return ZabbixServerNode.TryParse(address, CultureInfo.InvariantCulture, out ZabbixServerNode node) ? new ZabbixRedirect(node, number, Reset: false) : null;
    }

    // zbx_is_uint64 on the value's text: a JSON number or a string of digits.
    private static ulong? Unsigned(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.Number when reader.TryGetUInt64(out ulong number) => number,
        JsonTokenType.String when Utf8Parser.TryParse(reader.ValueSpan, out ulong number, out int consumed) && consumed == reader.ValueSpan.Length => number,
        _ => null,
    };

    /// <inheritdoc/>
    public override string ToString() => Reset ? "reset" : string.Create(CultureInfo.InvariantCulture, $"{Address} (revision {Revision})");
}
