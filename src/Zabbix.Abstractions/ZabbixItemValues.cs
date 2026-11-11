// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;

namespace Community.Zabbix;

/// <summary>Converts between a typed value and the text Zabbix carries, without reflection, so it holds under native AOT.</summary>
internal static class ZabbixItemValues
{
    public static T Parse<T>(string text)
        where T : ISpanParsable<T>
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            if (typeof(T) == typeof(bool)) return (T)(object)ParseBoolean(text);
            if (typeof(T) == typeof(TimeSpan)) return (T)(object)TimeSpan.FromSeconds(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
            if (typeof(T) == typeof(DateTimeOffset)) return (T)(object)DateTimeOffset.FromUnixTimeSeconds(long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture));
            return T.Parse(text, CultureInfo.InvariantCulture);
        }
        catch (Exception failure) when (failure is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            throw new FormatException($"A value of this item is not a {typeof(T).Name}: the item holds another type on the server.", failure);
        }
    }

    public static string Format<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            string text => text,
            bool flag => flag ? "1" : "0",
            TimeSpan span => span.TotalSeconds.ToString("R", CultureInfo.InvariantCulture),
            DateTimeOffset time => time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            double number => Finite(number).ToString("R", CultureInfo.InvariantCulture),
            float number => Finite(number).ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static bool ParseBoolean(string text) => text.Trim() switch
    {
        "1" => true,
        "0" => false,
        _ => bool.Parse(text),
    };

    private static double Finite(double value)
        => double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Zabbix stores no NaN or infinity.");
}
