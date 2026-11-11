// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Tests.Conformance;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Community.Zabbix.Tests;

/// <summary>
/// Every buffer the libraries hold is a reviewed decision. A store is the buffer between collection and delivery; a
/// channel anywhere else is a second drop point with its own policy, which a user who sets the store's
/// <c>MaxItems</c>/<c>Overflow</c> does not control. Each <c>Channel.Create*</c> site is listed here with the reason it
/// exists, so a new one fails this rule until it is given one.
/// </summary>
public sealed partial class DesignConformanceTests
{
    private static readonly Dictionary<string, (int Sites, string Reason)> ReviewedChannels = new(StringComparer.Ordinal)
    {
        ["ZabbixMemoryStore.cs"] = (2, "the store itself: bounded by MaxItems under its Overflow, or unbounded"),
        ["ZabbixPersistentStore.cs"] = (3, "three coalescing signals of capacity one (work, wake, room), which hold no records"),
        ["ZabbixPoll.cs"] = (1, "the poll's cadence runs whatever its reader does; DropOldest sheds what a slow reader misses, counted (3006), and what it holds at stop is counted (3007)"),
        ["ZabbixTrapperSource.cs"] = (1, "the edge between the listener and the agent's pump, in Wait mode: a full edge is answered failed to the sender, never dropped silently"),
        ["ZabbixWatcher.cs"] = (1, "a watch's readers each take what was collected since their last read"),
    };

    [Fact]
    public void Every_channel_the_libraries_create_is_a_reviewed_buffer()
    {
        Dictionary<string, int> found = [];
        foreach (SyntaxTree tree in Sources.Library)
        {
            int sites = tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>().Count(IsChannelCreation);
            if (sites > 0) found[Path.GetFileName(tree.FilePath)] = sites;
        }

        AssertNone(found.Where(site => !ReviewedChannels.TryGetValue(site.Key, out (int Sites, string Reason) reviewed) || reviewed.Sites != site.Value)
            .Select(site => $"{site.Key}: {site.Value} channel(s), not the reviewed count; give each a reason in {nameof(ReviewedChannels)}")
            .Concat(ReviewedChannels.Keys.Where(file => !found.ContainsKey(file)).Select(file => $"{file}: reviewed, but creates no channel any more")));
    }

    private static bool IsChannelCreation(InvocationExpressionSyntax invocation)
        => invocation.Expression is MemberAccessExpressionSyntax
        {
            Expression: IdentifierNameSyntax { Identifier.ValueText: "Channel" },
            Name.Identifier.ValueText: "CreateBounded" or "CreateUnbounded" or "CreateUnboundedPrioritized" or "CreateBoundedPrioritized",
        };
}
