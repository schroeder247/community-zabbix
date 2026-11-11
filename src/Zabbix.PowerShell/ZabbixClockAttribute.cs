// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// A point in time (<c>-From</c>, <c>-Till</c>, <c>-Clock</c>), every one a <see cref="DateTimeOffset"/>: a date
/// (<c>Get-Date</c>'s <see cref="DateTime"/>, local unless it says UTC, or a <see cref="DateTimeOffset"/>), Unix seconds
/// (<c>1700000000</c>, a fraction is nanoseconds), the wire's <c>clock.ns</c> text, or a date string
/// (<c>yyyy-MM-dd HH:mm</c> or other invariant-culture text, local unless it carries an offset). PowerShell's own conversion takes
/// neither Unix seconds nor the <c>clock.ns</c> text, so <c>-From (Get-Date).AddDays(-1)</c> would not bind without this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixClockAttribute : ArgumentTransformationAttribute
{
    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        object? input = inputData is PSObject wrapped ? wrapped.BaseObject : inputData;
        try
        {
            return input switch
            {
                null or DateTimeOffset => input,
                DateTime time => time.Kind == DateTimeKind.Utc ? new DateTimeOffset(time) : new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Local)),
                sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => FromUnixSeconds(Convert.ToDecimal(input, CultureInfo.InvariantCulture)),
                string text when decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal seconds) => FromUnixSeconds(seconds),
                string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset time) => time,
                _ => throw NotATime(input),
            };
        }
        catch (Exception failure) when (failure is OverflowException or ArgumentOutOfRangeException)
        {
            throw NotATime(input);
        }
    }

    /// <summary>The second of <paramref name="time"/> with <paramref name="nanoseconds"/> within it, to the 100 ns a <see cref="DateTimeOffset"/> holds.</summary>
    /// <param name="time">The time.</param>
    /// <param name="nanoseconds">The nanoseconds within the second, Zabbix's <c>ns</c>.</param>
    /// <returns>The time.</returns>
    internal static DateTimeOffset WithNanoseconds(DateTimeOffset time, int nanoseconds)
        => DateTimeOffset.FromUnixTimeSeconds(time.ToUnixTimeSeconds()).AddTicks(nanoseconds / 100);

    private static DateTimeOffset FromUnixSeconds(decimal seconds)
    {
        decimal whole = decimal.Floor(seconds);
        return DateTimeOffset.FromUnixTimeSeconds((long)whole).AddTicks((long)((seconds - whole) * TimeSpan.TicksPerSecond));
    }

    private static ArgumentTransformationMetadataException NotATime(object? input)
        => new($"'{input}' is not a point in time: give a date, Unix seconds or clock.ns.");
}
