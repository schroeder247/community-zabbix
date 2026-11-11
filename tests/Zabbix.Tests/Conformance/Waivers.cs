// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Tests.Conformance;

/// <summary>
/// Known violations of a rule that are not fixed yet, each with the fix it needs. A waiver is not an exemption:
/// it names one offender exactly as its rule reports it, it fails the rule the day it goes stale (the offender was
/// fixed, so the waiver must go), and each one shows as a skipped row of <c>A_waived_violation_is_still_owned</c> so a test
/// run lists what is outstanding. The goal state of this file is empty.
/// </summary>
internal static class Waivers
{
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ByRule { get; } = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
    {
    };

    /// <summary>The rule's offenders less its waivers, plus every waiver that no longer matches an offender (stale).</summary>
    public static IEnumerable<string> Outstanding(string rule, IEnumerable<string> offenders)
    {
        IReadOnlyDictionary<string, string> waived = ByRule.TryGetValue(rule, out IReadOnlyDictionary<string, string>? found) ? found : new Dictionary<string, string>();
        List<string> all = [.. offenders];
        return all.Where(offender => !waived.ContainsKey(offender))
            .Concat(waived.Keys.Where(offender => !all.Contains(offender)).Select(offender => $"stale waiver (fixed: delete it from Waivers): {offender}"));
    }

    /// <summary>A theory row for <paramref name="rule"/>, skipped with its owner when waived.</summary>
    public static TheoryDataRow<string> Row(string rule, string offender)
        => ByRule.TryGetValue(rule, out IReadOnlyDictionary<string, string>? waived) && waived.TryGetValue(offender, out string? owner)
            ? new TheoryDataRow<string>(offender) { Skip = $"waived to {owner}" }
            : new TheoryDataRow<string>(offender);

    public static TheoryData<string, string> Rows()
    {
        var rows = new TheoryData<string, string>();
        foreach ((string rule, IReadOnlyDictionary<string, string> waived) in ByRule)
            foreach ((string offender, string owner) in waived)
                rows.Add(new TheoryDataRow<string, string>(rule, offender) { Skip = $"waived to {owner}" });
        // The goal state of this file is empty, but a [Theory] with no data is a harness failure (MTP: "No data found"),
        // not the "nothing outstanding" this file means when it is empty: one synthetic, always-skipped row keeps the
        // theory a theory without claiming a real offender exists.
        if (rows.Count == 0) rows.Add(new TheoryDataRow<string, string>("(none)", "(none)") { Skip = "no outstanding waivers" });
        return rows;
    }
}
