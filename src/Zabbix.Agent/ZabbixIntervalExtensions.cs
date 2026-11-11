// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;

namespace Community.Zabbix;

/// <summary>
/// Provides an agent's schedule for a <see cref="ZabbixInterval"/>: when an item with that update interval is due, as
/// Zabbix's agents compute it, flexible and scheduling intervals included.
/// </summary>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
public static class ZabbixIntervalExtensions
{
    extension(ZabbixInterval interval)
    {
        /// <summary>
        /// Gets when an item with this interval and no phase is due after <paramref name="now"/>: the next check with a seed
        /// of 0, so every item with the same interval falls due at the same instants.
        /// </summary>
        /// <param name="now">The instant to schedule from.</param>
        /// <param name="zone">The zone flexible and scheduling intervals are read in: the agent's local time.</param>
        /// <returns>The next instant, or <see langword="null"/> when the interval never falls due.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="zone"/> is <see langword="null"/>.</exception>
        /// <exception cref="FormatException">The interval isn't one an agent schedules: it holds a macro, or breaks a rule an agent keeps; the message is upstream's.</exception>
        public DateTimeOffset? GetNextCheck(DateTimeOffset now, TimeZoneInfo zone) => interval.GetNextCheck(now, zone, 0);

        /// <summary>
        /// Gets when an item with this interval is due after <paramref name="now"/>, as both Zabbix agents compute an active
        /// check's next instant: the cadence phased by <paramref name="seed"/>, so items with the same interval spread over
        /// it; a flexible interval replaces the cadence while its period lasts; a scheduling interval adds its own instants.
        /// Resolution is one second.
        /// </summary>
        /// <param name="now">The instant to schedule from.</param>
        /// <param name="zone">The zone flexible and scheduling intervals are read in: the agent's local time.</param>
        /// <param name="seed">The phase: the item's id.</param>
        /// <returns>The next instant, or <see langword="null"/> when the interval never falls due.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="zone"/> is <see langword="null"/>.</exception>
        /// <exception cref="FormatException">The interval isn't one an agent schedules: it holds a macro, or breaks a rule an agent keeps; the message is upstream's.</exception>
        public DateTimeOffset? GetNextCheck(DateTimeOffset now, TimeZoneInfo zone, long seed)
        {
            ArgumentNullException.ThrowIfNull(zone);
            return ZabbixItemDelay.TryParse(interval.ToString(), out ZabbixItemDelay? delay, out string? error)
                ? delay.GetNextCheck(now, zone, seed)
                : throw new FormatException(error);
        }
    }
}
