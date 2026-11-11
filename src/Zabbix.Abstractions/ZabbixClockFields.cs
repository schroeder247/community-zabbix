// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix;

/// <summary>
/// The two wire fields of a clock (<c>clock</c> and <c>ns</c>) behind a record's one <see cref="ZabbixClock"/> member: the
/// records read and write the two, and the member joins and splits them.
/// </summary>
internal static class ZabbixClockFields
{
    private const int NanosecondsPerSecond = 1_000_000_000;

    /// <summary>The instant of <paramref name="seconds"/> and <paramref name="nanoseconds"/>; none without the seconds.</summary>
    /// <param name="seconds">The wire's <c>clock</c>.</param>
    /// <param name="nanoseconds">The wire's <c>ns</c>, already checked; 0 when absent.</param>
    /// <returns>The instant, or <see langword="null"/> when the record carries no clock.</returns>
    public static ZabbixClock? Join(long? seconds, int? nanoseconds) => seconds is { } whole ? new ZabbixClock(whole, nanoseconds ?? 0) : null;

    /// <summary>The time of <paramref name="seconds"/> and <paramref name="nanoseconds"/>, to its 100 ns tick; none without the seconds.</summary>
    /// <param name="seconds">The wire's <c>clock</c>.</param>
    /// <param name="nanoseconds">The wire's <c>ns</c>, already checked; 0 when absent.</param>
    /// <returns>The time, or <see langword="null"/> when the record carries no clock.</returns>
    public static DateTimeOffset? Time(long? seconds, int? nanoseconds) => Join(seconds, nanoseconds)?.ToDateTimeOffset();

    /// <summary>The wire's two fields of <paramref name="time"/>.</summary>
    /// <param name="time">The time.</param>
    /// <returns>The seconds and the nanoseconds, both <see langword="null"/> for no time.</returns>
    public static (long? Seconds, int? Nanoseconds) Split(DateTimeOffset? time) => Split(time is { } given ? ZabbixClock.FromDateTimeOffset(given) : null);

    /// <summary>The wire's two fields of <paramref name="clock"/>.</summary>
    /// <param name="clock">The instant.</param>
    /// <returns>The seconds and the nanoseconds, both <see langword="null"/> for no instant.</returns>
    public static (long? Seconds, int? Nanoseconds) Split(ZabbixClock? clock) => (clock?.Seconds, clock?.Nanoseconds);

    /// <summary><paramref name="clock"/> at another second, its nanoseconds kept.</summary>
    /// <param name="clock">The instant so far.</param>
    /// <param name="seconds">The wire's <c>clock</c>.</param>
    /// <returns>The instant.</returns>
    public static ZabbixClock WithSeconds(ZabbixClock clock, long seconds) => new(seconds, clock.Nanoseconds);

    /// <summary><paramref name="clock"/> with other nanoseconds, its second kept.</summary>
    /// <param name="clock">The instant so far.</param>
    /// <param name="nanoseconds">The wire's <c>ns</c>.</param>
    /// <param name="wire">The field's wire name, for the message.</param>
    /// <returns>The instant.</returns>
    /// <exception cref="JsonException">The value is outside 0 to 999,999,999.</exception>
    public static ZabbixClock WithNanoseconds(ZabbixClock clock, int nanoseconds, string wire)
        => nanoseconds is >= 0 and < NanosecondsPerSecond ? new(clock.Seconds, nanoseconds) : throw Outside(nanoseconds, wire);

    /// <summary><paramref name="value"/>, when it is within a second.</summary>
    /// <param name="value">The nanoseconds a reply or a caller gave.</param>
    /// <param name="wire">The field's wire name, for the message.</param>
    /// <returns>The value.</returns>
    /// <exception cref="JsonException">The value is outside 0 to 999,999,999.</exception>
    public static int? Nanoseconds(int? value, string wire)
        => value is not { } given || given is >= 0 and < NanosecondsPerSecond ? value : throw Outside(given, wire);

    private static JsonException Outside(int value, string wire)
        => new($"The field '{wire}' holds {value}, which is not nanoseconds within a second (0 to 999,999,999).");
}
