// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Community.Zabbix.Tests;

/// <summary>
/// A <c>history.get</c> double with <c>CHistory.php</c>'s semantics: one value type per call (<c>history</c>, default 3),
/// <c>itemids</c>, inclusive whole-second <c>time_from</c>/<c>time_till</c>, <c>sortfield</c> <c>clock, ns</c> ascending, and
/// <c>limit</c> applied after the sort — so a page that saturates inside one second really does cut that second.
/// </summary>
internal sealed class FakeHistory
{
    private readonly List<(string ItemId, int Type, long Clock, int Ns, string Value)> _rows = [];

    public FakeHistory Add(string itemId, ZabbixItemValueType type, long clock, int ns, string value)
    {
        lock (_rows) _rows.Add((itemId, (int)type, clock, ns, value));
        return this;
    }

    public string Answer(JsonElement parameters)
    {
        int type = parameters.TryGetProperty("history", out JsonElement h) ? h.GetInt32() : 3;
        HashSet<string>? ids = parameters.TryGetProperty("itemids", out JsonElement i) ? [.. i.EnumerateArray().Select(id => id.GetString()!)] : null;
        long from = parameters.TryGetProperty("time_from", out JsonElement f) ? f.GetInt64() : long.MinValue;
        long till = parameters.TryGetProperty("time_till", out JsonElement t) ? t.GetInt64() : long.MaxValue;
        int limit = parameters.TryGetProperty("limit", out JsonElement l) ? l.GetInt32() : int.MaxValue;
        List<(string ItemId, int Type, long Clock, int Ns, string Value)> rows;
        lock (_rows)
        {
            rows = [.. _rows
                .Where(row => row.Type == type && (ids is null || ids.Contains(row.ItemId)) && row.Clock >= from && row.Clock <= till)
                .OrderBy(row => row.Clock).ThenBy(row => row.Ns)
                .Take(limit)];
        }
        var json = new StringBuilder("[");
        foreach ((string itemId, _, long clock, int ns, string value) in rows)
        {
            if (json.Length > 1) json.Append(',');
            json.Append(CultureInfo.InvariantCulture, $$"""{"itemid":"{{itemId}}","clock":"{{clock}}","ns":"{{ns}}","value":"{{value}}"}""");
        }
        return json.Append(']').ToString();
    }
}
