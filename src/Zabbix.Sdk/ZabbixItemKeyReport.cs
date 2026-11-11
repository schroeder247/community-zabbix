// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using Community.Zabbix.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix;

/// <summary>
/// Which item keys a read could not name, each reported once. An item whose key is not one this SDK can read (Zabbix
/// accepted it; <see cref="ZabbixItemKey"/> follows upstream's grammar, so this is a defect to report, not a case to
/// expect) is left out and logged at its first sighting, and every other item is answered. A one-shot
/// read has one of these per call; a watcher has one for its life, so a bad key is one log line, not one per tick.
/// </summary>
internal sealed class ZabbixItemKeyReport(ILogger logger)
{
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>The item's key, or <see langword="false"/> (logged once per item) when it has none this SDK can read.</summary>
    public bool TryRead(ZabbixItem item, out ZabbixItemKey key)
    {
        key = default;
        if (item.Key is not { } text) return false;
        if (ZabbixItemKey.TryParse(text, out key)) return true;
        bool first;
        lock (_gate) first = _reported.Add(item.ItemId?.ToString(CultureInfo.InvariantCulture) ?? text);
        if (first) TreeLog.ItemKeyRefused(logger, item.ItemId?.ToString(CultureInfo.InvariantCulture) ?? "?", item.HostName ?? item.HostId?.ToString(CultureInfo.InvariantCulture) ?? "?", text);
        return false;
    }
}
