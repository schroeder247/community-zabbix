// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Reflection;
using System.Text.Json;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Exposes a read record's <c>AdditionalFields</c> bag as first-class properties, so <c>$h.newfield</c> reads a wire
/// field that a newer Zabbix added and the record does not model, beside the record's own .NET properties.
/// </summary>
/// <remarks>
/// Registered once per read-record type through <c>&lt;TypeAdapter&gt;</c> in the generated <c>Community.Zabbix.types.ps1xml</c>,
/// the same shape <see cref="ZabbixRecordConverter"/> uses for <c>&lt;TypeConverter&gt;</c>, and the same supported seam
/// <c>Microsoft.PowerShell.Cim.CimInstanceAdapter</c> uses for a <c>CimInstance</c>'s dynamic properties. It is a
/// <see cref="PSPropertyAdapter"/> rather than a <c>ScriptProperty</c> per key because the bag's key set is whatever
/// the server sent, so the properties are computed per instance.
/// </remarks>
public sealed class ZabbixAdditionalFieldsAdapter : PSPropertyAdapter
{
    // A registered PSPropertyAdapter IS the record's adapted view — Get-Member, Format-List and Select-Object *
    // enumerate GetProperties and nothing else (the CLR properties drop to the "Base" view, so without them
    // `Get-ZbxHost | Get-Member -MemberType Properties` lists 0 members). So the adapter
    // carries the record's own public properties first, the same set PowerShell's own DotNetAdapter would have
    // shown, then the bag keys. PSObject.AsPSObject(baseObject).Properties cannot supply the first
    // half: that PSObject is adapted by THIS adapter again (the type registration is per type, not per
    // call), so it recurses. The PropertyInfo list is cached per concrete record type.
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> s_clrProperties = new();

    /// <inheritdoc/>
    public override Collection<PSAdaptedProperty> GetProperties(object baseObject)
    {
        ArgumentNullException.ThrowIfNull(baseObject);
        var result = new Collection<PSAdaptedProperty>();
        PropertyInfo[] own = Own(baseObject.GetType());
        // The bag itself is plumbing, not a field, and not a public property of a read record (IZabbixAdditionalFields
        // is implemented explicitly): each of its keys is listed below as the property it is.
        foreach (PropertyInfo property in own)
            result.Add(new PSAdaptedProperty(property.Name, property));
        if (Bag(baseObject) is { } bag)
        {
            foreach (string key in bag.Keys)
            {
                // Named as the records name their members (vendor_name is VendorName), the wire key kept as the tag.
                string name = ZabbixWireNames.MemberOf(key);
                if (!own.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new PSAdaptedProperty(name, key));
            }
        }
        return result;
    }

    /// <inheritdoc/>
    public override PSAdaptedProperty? GetProperty(object baseObject, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(baseObject);
        ArgumentException.ThrowIfNullOrEmpty(propertyName);
        if (Own(baseObject.GetType()).FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase)) is { } property)
            return new PSAdaptedProperty(property.Name, property);
        // The member name (VendorName) or the wire key itself (vendor_name), either case.
        if (Bag(baseObject) is not { } bag) return null;
        foreach (string key in bag.Keys)
        {
            string name = ZabbixWireNames.MemberOf(key);
            if (string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase) || string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase))
                return new PSAdaptedProperty(name, key);
        }
        return null;
    }

    /// <inheritdoc/>
    public override string GetPropertyTypeName(PSAdaptedProperty adaptedProperty)
    {
        ArgumentNullException.ThrowIfNull(adaptedProperty);
        if (adaptedProperty.Tag is PropertyInfo property) return property.PropertyType.FullName ?? property.PropertyType.Name;
        // The type of the value the shell reads (ZabbixJsonShape.ToShell), not the JsonElement it is kept as.
        JsonElement element = Bag(adaptedProperty.BaseObject) is { } bag && bag.TryGetValue((string)adaptedProperty.Tag, out JsonElement found) ? found : default;
        return element.ValueKind switch
        {
            JsonValueKind.String => typeof(string).FullName!,
            JsonValueKind.Number => element.TryGetInt64(out _) ? typeof(long).FullName! : typeof(double).FullName!,
            JsonValueKind.True or JsonValueKind.False => typeof(bool).FullName!,
            JsonValueKind.Object => typeof(PSObject).FullName!,
            JsonValueKind.Array => typeof(object[]).FullName!,
            _ => typeof(object).FullName!,
        };
    }

    /// <inheritdoc/>
    public override object? GetPropertyValue(PSAdaptedProperty adaptedProperty)
    {
        ArgumentNullException.ThrowIfNull(adaptedProperty);
        if (adaptedProperty.Tag is PropertyInfo property)
        {
            // A modelled property the description could only type as raw JSON (action filters and operations, role
            // rules, maintenance time periods, dashboard widgets, host inventory, ...) reaches the shell the same way a
            // bag value does: as navigable objects keyed by wire name, never as a bare JsonElement.
            object? value = property.GetValue(adaptedProperty.BaseObject);
            return value is JsonElement modelled ? ZabbixJsonShape.ToShell(modelled) : value;
        }
        string key = (string)adaptedProperty.Tag;
        return Bag(adaptedProperty.BaseObject) is { } bag && bag.TryGetValue(key, out JsonElement element) ? ZabbixJsonShape.ToShell(element) : null;
    }

    /// <inheritdoc/>
    public override void SetPropertyValue(PSAdaptedProperty adaptedProperty, object value)
    {
        ArgumentNullException.ThrowIfNull(adaptedProperty);
        if (adaptedProperty.Tag is PropertyInfo { SetMethod.IsPublic: true } property)
        {
            // PowerShell's own conversion, as for any property it sets: '10084' becomes the ulong a HostId holds
            property.SetValue(adaptedProperty.BaseObject, LanguagePrimitives.ConvertTo(value, property.PropertyType, CultureInfo.InvariantCulture));
            return;
        }
        throw new NotSupportedException($"'{adaptedProperty.Name}' is read-only, the same as every other property of a Get- result the record does not let you set.");
    }

    /// <inheritdoc/>
    public override bool IsGettable(PSAdaptedProperty adaptedProperty) => true;

    /// <inheritdoc/>
    public override bool IsSettable(PSAdaptedProperty adaptedProperty) => adaptedProperty?.Tag is PropertyInfo { SetMethod.IsPublic: true };

    /// <inheritdoc/>
    public override Collection<string> GetTypeNameHierarchy(object baseObject)
    {
        ArgumentNullException.ThrowIfNull(baseObject);
        var names = new Collection<string>();
        for (Type? type = baseObject.GetType(); type is not null; type = type.BaseType) names.Add(type.FullName!);
        return names;
    }

    // The bag itself is not one of the record's fields: its keys are (above).
    private static PropertyInfo[] Own(Type type) => s_clrProperties.GetOrAdd(type, static t => t
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0
            && !(p.Name == nameof(IZabbixAdditionalFields.AdditionalFields) && p.PropertyType == typeof(IDictionary<string, JsonElement>)))
        .ToArray());

    // every generated read record implements IZabbixAdditionalFields (ZabbixApiEmitter); no reflection by name
    private static IDictionary<string, JsonElement>? Bag(object? baseObject) => (baseObject as IZabbixAdditionalFields)?.AdditionalFields;
}
