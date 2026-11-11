// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Tests.Conformance;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Community.Zabbix.Tests;

/// <summary>
/// Only a token's owner decides that a cancellation is a stop. A library catch that takes an
/// <see cref="OperationCanceledException"/> as "stopped", or a catch-all that lets every cancellation through as "not a
/// failure", asks whose cancellation it is: its filter tests an owned token's <see cref="CancellationToken.IsCancellationRequested"/>.
/// Otherwise a callee's own timeout (an <see cref="HttpClient"/> deadline throws <see cref="TaskCanceledException"/>) is
/// mistaken for the caller's stop, and a loop ends silently on it. A clause that rethrows, or that sits inside a catch that does, hands the question on and passes.
/// </summary>
public sealed partial class DesignConformanceTests
{
    private static readonly string[] CancellationTypes = ["OperationCanceledException", "TaskCanceledException"];

    [Fact]
    public void Every_cancellation_catch_asks_whose_cancellation_it_is()
        => AssertNone(Waivers.Outstanding(nameof(Every_cancellation_catch_asks_whose_cancellation_it_is), UnownedCancellationCatches(Sources.Library)));

    [Fact]
    public void The_cancellation_rule_finds_a_catch_that_does_not_ask_whose_cancellation_it_is()
    {
        const string probe = """
            class Probe
            {
                async Task Run(CancellationToken stopping, Func<Task> work)
                {
                    try { await work(); } catch (OperationCanceledException) { }                                              // offender
                    try { await work(); } catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
                    try { await work(); } catch (Exception failure) when (failure is not OperationCanceledException) { Log(failure); }   // offender
                    try { await work(); } catch (Exception failure) when (!(failure is OperationCanceledException && stopping.IsCancellationRequested)) { Log(failure); }
                    try { await work(); } catch (TaskCanceledException) when (Other()) { }                                    // offender
                    try { await work(); } catch (OperationCanceledException) { Undo(); throw; }
                    try { await work(); } catch (InvalidOperationException) { }
                    try { await work(); } catch (IOException) { try { await work(); } catch (OperationCanceledException) { } throw; }
                }
            }
            """;

        List<string> found = [.. UnownedCancellationCatches([Sources.ParseText(probe, "Probe.cs")])];

        Assert.Equal(["Probe.cs:5", "Probe.cs:7", "Probe.cs:9"], found);
    }

    internal static IEnumerable<string> UnownedCancellationCatches(IEnumerable<SyntaxTree> trees)
    {
        foreach (SyntaxTree tree in trees)
        {
            foreach (CatchClauseSyntax clause in tree.GetRoot().DescendantNodes().OfType<CatchClauseSyntax>())
            {
                string? caught = clause.Declaration?.Type switch
                {
                    IdentifierNameSyntax name => name.Identifier.ValueText,
                    QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                    _ => null,
                };
                bool takesCancellation = caught is not null && CancellationTypes.Contains(caught);
                bool letsCancellationThrough = IsCatchAll(clause.Declaration?.Type) && clause.Filter is { } filter
                    && filter.FilterExpression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(name => CancellationTypes.Contains(name.Identifier.ValueText));
                if (!takesCancellation && !letsCancellationThrough) continue;
                if (AsksTheToken(clause.Filter) || Throws(clause.Block) || InsideARethrow(clause)) continue;
                yield return $"{Path.GetFileName(tree.FilePath)}:{clause.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
            }
        }
    }

    // A best-effort step inside a catch that rethrows (sending a TLS alert before the handshake's failure is thrown): the
    // failure in hand is thrown whatever became of the step.
    private static bool InsideARethrow(CatchClauseSyntax clause)
        => clause.Ancestors().OfType<CatchClauseSyntax>().Any(outer => Throws(outer.Block));

    private static bool AsksTheToken(CatchFilterClauseSyntax? filter)
        => filter is not null && filter.FilterExpression.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
            .Any(access => access.Name.Identifier.ValueText == nameof(CancellationToken.IsCancellationRequested));
}
