// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// A time of day (<c>-StartTime</c> on a maintenance period and a report), every one a <see cref="TimeOnly"/>: a number is
/// seconds after midnight (<c>-StartTime 7200</c>), a string is a number of seconds or a time (<c>'02:00'</c>), a
/// <see cref="TimeSpan"/> within one day is that offset from midnight, and a <see cref="TimeOnly"/> passes through.
/// Every <see cref="TimeOnly"/> parameter of the modules carries it (<c>ParameterShapeTests</c>). PowerShell's own
/// conversion reads an integer as ticks (<c>[TimeOnly]7200</c> is 720 microseconds after midnight), which a transformation
/// attribute corrects. A value outside one day is refused at binding.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixTimeOfDayAttribute : ArgumentTransformationAttribute
{
    private const long SecondsPerDay = 86_400;

    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        object? input = inputData is PSObject wrapped ? wrapped.BaseObject : inputData;
        TimeOnly? time = input switch
        {
            null => null,
            TimeOnly given => given,
            TimeSpan offset when offset >= TimeSpan.Zero && offset < TimeSpan.FromDays(1) => TimeOnly.FromTimeSpan(offset),
            sbyte or byte or short or ushort or int or uint or long or ulong => FromSeconds(Convert.ToDecimal(input, CultureInfo.InvariantCulture)),
            string text when long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) => FromSeconds(seconds),
            string text when TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly parsed) => parsed,
            _ => null,
        };
        if (input is null) return null;
        return time ?? throw new ArgumentTransformationMetadataException($"'{input}' is not a time of day: give seconds after midnight (7200) or a time ('02:00').");
    }

    private static TimeOnly? FromSeconds(decimal seconds)
        => seconds is >= 0 and < SecondsPerDay ? new TimeOnly((long)seconds * TimeSpan.TicksPerSecond) : null;
}
