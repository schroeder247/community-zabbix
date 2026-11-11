// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Provides named values for two free-text wire fields: map colors (<c>-FontColor DarkGray</c> → <c>333333</c>) and
/// dashboard widget types (<c>-Type ProblemsBySeverity</c> → <c>problemsbysv</c>). The wire field stays a string; a name
/// is a spelling the shell translates, and the wire value itself is always accepted, so <c>-ColorRaw</c>/<c>-TypeRaw</c>
/// are aliases of the same parameter.
/// </summary>
internal static class ZabbixNamedValues
{
    /// <summary>
    /// The color names and their hex, matched case-insensitively.
    /// </summary>
    public static FrozenDictionary<string, string> MapColors { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Black"] = "000000",
        ["White"] = "FFFFFF",
        ["Red"] = "DD0000",
        ["Green"] = "00AA00",
        ["Blue"] = "0000DD",
        ["Yellow"] = "FFFF00",
        ["Orange"] = "FF8800",
        ["Cyan"] = "00DDDD",
        ["Purple"] = "AA00AA",
        ["Gray"] = "999999",
        ["DarkGray"] = "333333",
        ["LightGray"] = "CCCCCC",
        ["DarkRed"] = "AA0000",
        ["DarkGreen"] = "009900",
        ["BrightGreen"] = "00CC00",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Widget type names and their wire ids, including <c>DataOverview</c> (7.0 only) and <c>ScatterPlot</c> (8.0). The
    /// ids are the <c>id</c> of each <c>ui/widgets/*/manifest.json</c> in Zabbix 7.0, 7.4 and 8.0. Matched
    /// case-insensitively.
    /// </summary>
    public static FrozenDictionary<string, string> WidgetTypes { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ActionLog"] = "actionlog",
        ["Clock"] = "clock",
        ["DataOverview"] = "dataover",
        ["Discovery"] = "discovery",
        ["FavoriteGraphs"] = "favgraphs",
        ["FavoriteMaps"] = "favmaps",
        ["Gauge"] = "gauge",
        ["GeoMap"] = "geomap",
        ["Graph"] = "graph",
        ["GraphPrototype"] = "graphprototype",
        ["Honeycomb"] = "honeycomb",
        ["HostAvailability"] = "hostavail",
        ["HostCard"] = "hostcard",
        ["HostNavigator"] = "hostnavigator",
        ["Item"] = "item",
        ["ItemCard"] = "itemcard",
        ["ItemHistory"] = "itemhistory",
        ["ItemNavigator"] = "itemnavigator",
        ["Map"] = "map",
        ["NavigationTree"] = "navtree",
        ["PieChart"] = "piechart",
        ["Problems"] = "problems",
        ["ProblemHosts"] = "problemhosts",
        ["ProblemsBySeverity"] = "problemsbysv",
        ["ScatterPlot"] = "scatterplot",
        ["SlaReport"] = "slareport",
        ["SvgGraph"] = "svggraph",
        ["SystemInfo"] = "systeminfo",
        ["TopHosts"] = "tophosts",
        ["TopItems"] = "topitems",
        ["TopTriggers"] = "toptriggers",
        ["TriggerOverview"] = "trigover",
        ["Url"] = "url",
        ["WebMonitoring"] = "web",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // The names, ordinally sorted, for completion.
    internal static string[] Names(FrozenDictionary<string, string> table) => [.. table.Keys.Order(StringComparer.Ordinal)];
}
