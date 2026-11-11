// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// A duration (<c>-Timeout</c>, <c>-Interval</c>, <c>-RefreshInterval</c>), every one a <see cref="TimeSpan"/>: a number is
/// seconds (<c>-Timeout 10</c>), a string is a number of seconds, Zabbix's own time suffix (<c>'30s'</c>, <c>'5m'</c>,
/// <c>'1h'</c>, <c>'1d'</c>, <c>'1w'</c>: a whole number and one of <c>s</c>, <c>m</c>, <c>h</c>, <c>d</c>, <c>w</c>, as an item's
/// update interval is written) or a <see cref="TimeSpan"/> (<c>'00:00:30'</c>), and a <see cref="TimeSpan"/> passes through.
/// Every <see cref="TimeSpan"/> parameter of the modules carries it (<c>ParameterShapeTests</c>). PowerShell's own conversion reads an integer as ticks (<c>[timespan]30</c> is
/// three microseconds), the conversion the shell gets wrong by default, which a transformation attribute corrects. A
/// duration of zero or less is refused at binding: none of these waits means anything at zero. A parameter whose range
/// is narrower (<c>Get-ZbxAgentItem -Timeout</c>, 1 to 300 seconds) says so with <see cref="MinimumSeconds"/> and
/// <see cref="MaximumSeconds"/>; <c>ValidateRange</c> cannot compare a <see cref="TimeSpan"/> with numbers.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixDurationAttribute : ArgumentTransformationAttribute
{
    /// <summary>The shortest duration taken, in seconds; any duration greater than zero when 0.</summary>
    public double MinimumSeconds { get; set; }

    /// <summary>The longest duration taken, in seconds; no limit when 0.</summary>
    public double MaximumSeconds { get; set; }

    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        object? input = inputData is PSObject wrapped ? wrapped.BaseObject : inputData;
        TimeSpan? duration = input switch
        {
            TimeSpan given => given,
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
                => TimeSpan.FromSeconds(Convert.ToDouble(input, CultureInfo.InvariantCulture)),
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) => TimeSpan.FromSeconds(seconds),
            string text when ZabbixDuration.TryParse(text.Trim(), out ZabbixDuration suffixed) && suffixed.Length is { } length => length,
            string text when TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan parsed) => parsed,
            _ => null,
        };
        if (duration is not { } value)
            throw new ArgumentTransformationMetadataException($"'{input}' is not a duration: give seconds (30), a Zabbix time suffix ('30s', '5m', '1h') or a time span ('00:00:30').");
        if (value <= TimeSpan.Zero)
            throw new ArgumentTransformationMetadataException($"'{input}' is not a duration greater than zero.");
        if ((MinimumSeconds > 0 && value < TimeSpan.FromSeconds(MinimumSeconds)) || (MaximumSeconds > 0 && value > TimeSpan.FromSeconds(MaximumSeconds)))
            throw new ArgumentTransformationMetadataException(string.Create(CultureInfo.InvariantCulture, $"'{input}' is not a duration of {MinimumSeconds} to {MaximumSeconds} seconds."));
        return value;
    }
}
