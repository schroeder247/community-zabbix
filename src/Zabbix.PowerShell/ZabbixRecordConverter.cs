// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Management.Automation;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Converts a <see cref="Hashtable"/>, an <see cref="OrderedDictionary"/> or a <c>[pscustomobject]</c> to any SDK
/// request, parameters or nested record type, so PowerShell's own parameter binder can bind
/// <c>[Community.Zabbix.ZabbixHostCreateRequest]@{ host = 'h' }</c> or a bare hashtable to a record-typed parameter, at any
/// nesting depth.
/// </summary>
/// <remarks>
/// Registered per record type through <c>&lt;TypeConverter&gt;</c> in the generated <c>Community.Zabbix.types.ps1xml</c>, because
/// the records are the SDK's generated output, not this module's. <see cref="ConvertFrom"/> hands the value to
/// <see cref="ZabbixJsonBinder.Bind(object?,JsonTypeInfo)"/>, the contract that (de)serializes the wire. A single
/// <see cref="Hashtable"/> for a record-list target is one element, not a sequence of <see cref="DictionaryEntry"/>
/// values. <see cref="LanguagePrimitives"/>' built-in conversion fails on an <c>IReadOnlyList&lt;T&gt;</c> property, and
/// a converter on the nested element type is never consulted, so the converter owns the whole object graph.
/// <c>ConvertTo</c> is not supported (the reverse direction is <see cref="ZabbixAdditionalFieldsAdapter"/>).
/// </remarks>
public sealed class ZabbixRecordConverter : PSTypeConverter
{
    // The converter's destinationType is only known at run time, from whatever PowerShell asks CanConvertFrom about, so
    // its contract is looked up by type in the SDK's JSON contract (ZabbixJson.Options, which caches each one): a type
    // the API model does not have is not this converter's.

    /// <inheritdoc/>
    public override bool CanConvertFrom(object sourceValue, Type destinationType)
    {
        ArgumentNullException.ThrowIfNull(destinationType);
        if (sourceValue is null) return false;
        Type elementType = ZabbixJsonBinder.ListElementType(destinationType) ?? destinationType;
        if (!ZabbixContracts.TryFind(elementType, out _)) return false;
        object? unwrapped = ZabbixJsonBinder.Unwrap(sourceValue);
        if (elementType == destinationType) return IsRecordShaped(unwrapped);
        // A collection target: a single IDictionary is one element (a Hashtable is itself IEnumerable), never enumerated as several;
        // anything else enumerable must be a sequence where every item is itself record-shaped.
        return unwrapped switch
        {
            IDictionary => true,
            string => false,
            IEnumerable many => many.Cast<object?>().All(item => IsRecordShaped(ZabbixJsonBinder.Unwrap(item))),
            _ => false,
        };
    }

    /// <inheritdoc/>
    public override bool CanConvertTo(object sourceValue, Type destinationType) => false;

    /// <inheritdoc/>
    public override object? ConvertFrom(object sourceValue, Type destinationType, IFormatProvider formatProvider, bool ignoreCase)
    {
        ArgumentNullException.ThrowIfNull(destinationType);
        try
        {
            Type elementType = ZabbixJsonBinder.ListElementType(destinationType) ?? destinationType;
            if (!ZabbixContracts.TryFind(elementType, out JsonTypeInfo? typeInfo))
                throw new PSInvalidCastException(string.Create(CultureInfo.InvariantCulture,
                    $"{elementType.Name} is not part of the SDK's JSON contract (ZabbixJson) — cannot convert to it."));

            object? unwrapped = ZabbixJsonBinder.Unwrap(sourceValue);
            if (elementType == destinationType) return ZabbixJsonBinder.Bind(unwrapped, typeInfo);

            IEnumerable<object?> items = unwrapped is IDictionary or string
                ? new List<object?> { unwrapped }
                : ((IEnumerable)unwrapped!).Cast<object?>();
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
            foreach (object? item in items) list.Add(ZabbixJsonBinder.Bind(item, typeInfo));
            return CoerceCollection(list, destinationType, elementType);
        }
        // No `catch when (ignoreCase) return null`: PowerShell passes ignoreCase = true on every cast and parameter
        // bind, so that swallow would turn every refused conversion — an unknown -Filter key, a bad nested value —
        // into a silent $null. A PSTypeConverter reports failure by throwing
        // PSInvalidCastException, which the binder wraps with the parameter name.
        catch (PSInvalidCastException)
        {
            throw;
        }
        catch (Exception failure)
        {
            throw new PSInvalidCastException(string.Create(CultureInfo.InvariantCulture,
                $"'{sourceValue}' does not convert to {destinationType.Name}: {failure.Message}"), failure);
        }
    }

    /// <inheritdoc/>
    public override object ConvertTo(object sourceValue, Type destinationType, IFormatProvider formatProvider, bool ignoreCase)
        => throw new NotSupportedException($"{nameof(ZabbixRecordConverter)} only converts FROM a loose value TO an SDK record, never back.");

    private static object CoerceCollection(IList list, Type destinationType, Type elementType)
    {
        if (destinationType.IsArray)
        {
            Array array = Array.CreateInstance(elementType, list.Count);
            list.CopyTo(array, 0);
            return array;
        }
        if (destinationType.IsAssignableFrom(list.GetType())) return list;
        // IReadOnlyList<T>/IReadOnlyCollection<T>/IList<T>/ICollection<T>/IEnumerable<T>: the List<T> built above
        // already implements every one of them.
        return list;
    }

    // Hashtable/OrderedDictionary/any other IDictionary, or a PSCustomObject (bare, or — the ordinary shell shape,
    // ZabbixJsonBinder.Unwrap's own contract — wrapped in a PSObject whose BaseObject IS the PSCustomObject marker).
    private static bool IsRecordShaped(object? value) => value switch
    {
        null => false,
        IDictionary => true,
        PSObject { BaseObject: PSCustomObject } => true,
        PSCustomObject => true,
        _ => false,
    };
}
