// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Globalization;
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Binds a <c>[Flags]</c> enum parameter from an array of member names (<c>Unencrypted,Psk</c>, PowerShell's own comma
/// list — an unquoted comma list is an <see cref="object"/>[], which a scalar <c>[Flags]</c> enum parameter rejects by
/// default with "Cannot convert the System.Object[] value"), a single name, or a bitmask integer already combining
/// several bits. Combines every item with bitwise-or; nothing here resolves against a server, so it costs nothing at
/// parameter-binding time.
/// </summary>
/// <typeparam name="TEnum">The <c>[Flags]</c> enum.</typeparam>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixFlagsAttribute<TEnum> : ArgumentTransformationAttribute
    where TEnum : struct, Enum
{
    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        object? input = inputData is PSObject wrapped ? wrapped.BaseObject : inputData;
        if (input is null) return null;
        long combined = 0;
        bool any = false;
        foreach (object? item in input is IEnumerable many and not string ? many : new[] { input })
        {
            if (!TryBit(item, out long bit)) throw Refuse(item);
            combined |= bit;
            any = true;
        }
        return any ? Enum.ToObject(typeof(TEnum), combined) : null;
    }

    private static bool TryBit(object? item, out long bit)
    {
        switch (item is PSObject wrapped ? wrapped.BaseObject : item)
        {
            case TEnum value:
                bit = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                return true;
            case string text when Enum.TryParse(text, ignoreCase: true, out TEnum parsed):
                bit = Convert.ToInt64(parsed, CultureInfo.InvariantCulture);
                return true;
            case string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number):
                bit = number;
                return true;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                bit = Convert.ToInt64(item, CultureInfo.InvariantCulture);
                return true;
            default:
                bit = 0;
                return false;
        }
    }

    private static ArgumentTransformationMetadataException Refuse(object? value)
        => new(string.Create(CultureInfo.InvariantCulture,
            $"'{value}' ({value?.GetType().Name ?? "null"}) is not a {typeof(TEnum).Name} member, its name, or a bitmask combining several."));
}
