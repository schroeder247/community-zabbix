// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel.DataAnnotations;

namespace Community.Zabbix.Hosting;

/// <summary>
/// The duration checks of the hosts' options, for their <see cref="IValidatableObject.Validate"/>: <c>[Range]</c> over a
/// <see cref="TimeSpan"/> converts its bounds through a type converter, which is not trim-safe, so the bounds are code.
/// The longest a duration may be is what a <see cref="CancellationTokenSource"/> and a <see cref="PeriodicTimer"/> take.
/// </summary>
internal static class ZabbixOptionsRanges
{
    /// <summary>The longest duration a timer or a deadline takes (<see cref="int.MaxValue"/> milliseconds, about 24.8 days).</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>A failure when <paramref name="value"/> is not above zero (or, with <paramref name="zeroAllowed"/>, below it) or beyond <paramref name="longest"/>.</summary>
    public static ValidationResult? Duration(TimeSpan value, string member, bool zeroAllowed = false, TimeSpan? longest = null)
    {
        TimeSpan most = longest ?? Longest;
        bool low = zeroAllowed ? value < TimeSpan.Zero : value <= TimeSpan.Zero;
        return low || value > most
            ? new ValidationResult($"{member} must be {(zeroAllowed ? "zero or more" : "more than zero")} and at most {most}; it is {value}.", [member])
            : null;
    }

    /// <summary>A failure when <paramref name="rules"/> are not Zabbix's <c>AllowKey</c>/<c>DenyKey</c> lines (<see cref="ZabbixKeyRules.Parse"/>).</summary>
    public static ValidationResult? KeyRules(IEnumerable<string> rules, string member)
    {
        try
        {
            _ = ZabbixKeyRules.Parse(rules);
            return null;
        }
        catch (FormatException invalid)
        {
            return new ValidationResult($"{member}: {invalid.Message}", [member]);
        }
    }

    /// <summary>The failures among <paramref name="checks"/>.</summary>
    public static IEnumerable<ValidationResult> Failures(params ValidationResult?[] checks)
    {
        foreach (ValidationResult? check in checks)
            if (check is not null) yield return check;
    }
}
