// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// What the server answers on its wire when it says <c>success</c>: the data, an <c>info</c> line, and a <c>total</c>
/// where the request counts (<c>queue.get</c> details). The shape is <c>zbx_send_response_json</c>'s
/// (<c>src/libs/zbxcommshigh/commshigh.c</c>) and what <c>CZabbixServer::normalizeResponse</c> accepts; a
/// <c>failed</c> reply never becomes one of these — it is a <see cref="ZabbixServerRefusedException"/>.
/// </summary>
/// <param name="Response">Always <c>success</c>.</param>
/// <param name="Data">The data, when the request has any.</param>
/// <param name="Info">The server's info line, when it sent one.</param>
/// <param name="Total">The total, when the request counts.</param>
public sealed record ZabbixServerReply(
    [property: JsonPropertyName("response")] string? Response,
    [property: JsonPropertyName("data")] JsonElement? Data,
    [property: JsonPropertyName("info")] string? Info,
    [property: JsonPropertyName("total")] long? Total)
{
    /// <summary>Whether <paramref name="other"/> is the same reply, its data by its JSON.</summary>
    /// <param name="other">The other reply.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixServerReply? other)
        => other is not null && string.Equals(Response, other.Response, StringComparison.Ordinal) && ZabbixValueEquality.JsonEqual(Data, other.Data)
            && string.Equals(Info, other.Info, StringComparison.Ordinal) && Total == other.Total;

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Response, StringComparer.Ordinal);
        ZabbixValueEquality.AddJson(ref hash, Data);
        hash.Add(Info, StringComparer.Ordinal);
        hash.Add(Total);
        return hash.ToHashCode();
    }
}
