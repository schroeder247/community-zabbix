// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using Community.Zabbix.Tests.Conformance;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Tests;

/// <summary>
/// The event-id catalogue (docs/design/decisions.md, "Each package owns a block of event ids"): an operator filters by id,
/// so each package's log events stay in its block, and a new event lands where its package's others are. A hot path
/// (every request, every batch, every tick) never logs at Information: its counts are a meter's, and the event is Debug.
/// </summary>
public sealed partial class DesignConformanceTests
{
    /// <summary>Each package's block, inclusive. Mirrors the table in decisions.md.</summary>
    internal static readonly IReadOnlyDictionary<string, (int First, int Last)> EventIdBlocks = new Dictionary<string, (int First, int Last)>(StringComparer.Ordinal)
    {
        ["Community.Zabbix.Wire"] = (1000, 1999),
        ["Community.Zabbix.Agent"] = (2000, 3999),
        ["Community.Zabbix.Sdk"] = (4000, 5999),
        ["Community.Zabbix.Proxy"] = (6000, 7999),
        ["Community.Zabbix.PowerShell"] = (14000, 14999),
    };

    /// <summary>
    /// The Information events a library may write: each is rare (once per process, per composition or per redirect), so
    /// it has long-term value at the level production keeps. Anything else that runs per request, batch or tick is Debug.
    /// </summary>
    private static readonly HashSet<int> RareInformationEvents = [1109, 1110, 4004, 4005, 5004];

    /// <summary>
    /// The Error events a library may write: each is an outcome nobody else sees, because the library swallowed it to keep a
    /// loop, a listener or a store going. A failure whose exception is handed back to the caller is the caller's to judge,
    /// so the library logs it at Debug; logging it at Error would write it twice, once per retry, as an error nobody
    /// failed to handle (Logging in .NET, "Log level").
    /// </summary>
    private static readonly HashSet<int> TerminalErrorEvents = [2000, 3002, 3003, 3005, 3100, 3103, 3302, 3305, 5003, 6005, 6008, 6102, 7030, 7040, 14201];

    [Fact]
    public void Only_a_swallowed_outcome_is_logged_as_an_error()
    {
        List<string> offenders = [.. LoggerMessages()
            .Where(entry => entry.Attribute.Level >= LogLevel.Error && !TerminalErrorEvents.Contains(entry.Attribute.EventId))
            .Select(entry => $"{entry.Attribute.EventId} {Describe(entry.Method)}")];

        AssertNone(offenders);
    }

    [Fact]
    public void Every_log_event_stays_in_its_package_s_block()
        => AssertNone(Waivers.Outstanding(nameof(Every_log_event_stays_in_its_package_s_block), EventsOutsideTheirBlock(LoggerMessages())));

    [Fact]
    public void The_block_rule_finds_an_event_outside_its_package_s_block()
    {
        MethodInfo stray = typeof(StrayEventProbe).GetMethod(nameof(StrayEventProbe.Stray))!;

        Assert.Equal(["Community.Zabbix.Tests.StrayEventProbe: 4321 outside the block of Zabbix.Tests (none)"],
            EventsOutsideTheirBlock([(stray, stray.GetCustomAttribute<LoggerMessageAttribute>()!)]));
    }

    [Fact]
    public void Hot_path_events_are_not_information()
    {
        List<string> offenders = [.. LoggerMessages()
            .Where(entry => entry.Attribute.Level == LogLevel.Information && !RareInformationEvents.Contains(entry.Attribute.EventId))
            .Where(entry => ClientPackages.Contains(entry.Method.DeclaringType!.Assembly.GetName().Name))
            .Select(entry => $"{entry.Attribute.EventId} {Describe(entry.Method)}")];

        AssertNone(offenders);
    }

    /// <summary>
    /// The catalogue an operator filters by, as shipped: each event's id, package, level and name, one line each in
    /// <c>events.txt</c>. Uniqueness across the assemblies is
    /// <see cref="Logger_message_event_ids_are_unique_across_the_assemblies"/>; this pins the rest, so a renamed method
    /// (the default <see cref="LoggerMessageAttribute.EventName"/>), a moved id or a changed level is a reviewed change
    /// to the file, never a silent one.
    /// </summary>
    [Fact]
    public void The_event_catalogue_matches_its_snapshot()
    {
        string path = Path.Combine(Repository.Root, "tests", "Zabbix.Tests", "events.txt");
        List<string> expected = [.. File.ReadLines(path).Where(line => line.Length > 0 && !line.StartsWith('#'))];

        Assert.Equal(expected, EventCatalogue());
    }

    internal static List<string> EventCatalogue()
        => [.. LoggerMessages()
            .OrderBy(entry => entry.Attribute.EventId)
            .Select(entry => string.Join('\t',
                entry.Attribute.EventId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                entry.Method.DeclaringType!.Assembly.GetName().Name,
                entry.Attribute.Level,
                entry.Attribute.EventName ?? entry.Method.Name))];

    internal static IEnumerable<string> EventsOutsideTheirBlock(IEnumerable<(MethodInfo Method, LoggerMessageAttribute Attribute)> messages)
        => messages
            .GroupBy(entry => entry.Method.DeclaringType!)
            .Select(group =>
            {
                string package = group.Key.Assembly.GetName().Name!;
                (int First, int Last)? block = EventIdBlocks.TryGetValue(package, out (int First, int Last) found) ? found : null;
                int[] outside = [.. group.Select(entry => entry.Attribute.EventId).Where(id => block is not { } known || id < known.First || id > known.Last).Order()];
                string range = block is { } shown ? $"{shown.First}-{shown.Last}" : "(none)";
                return outside.Length == 0 ? null : $"{group.Key.FullName}: {string.Join(", ", outside)} outside the block of {package} {range}";
            })
            .OfType<string>()
            .Order(StringComparer.Ordinal);
}

/// <summary>A log event the block rule must find: the test assembly has no block.</summary>
internal static partial class StrayEventProbe
{
    [LoggerMessage(EventId = 4321, Level = LogLevel.Debug, Message = "Stray")]
    public static partial void Stray(ILogger logger);
}
