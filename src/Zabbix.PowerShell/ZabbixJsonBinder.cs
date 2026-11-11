// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Management.Automation;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Binds a loose PowerShell value to any SDK request or parameters type through that type's own JSON contract.
/// </summary>
/// <remarks>
/// <see cref="RemapKeys"/> renames every <see cref="Hashtable"/> key (a C# property name, case-insensitive, or a wire
/// name) to the wire name the target type declares, recursively; <c>ZabbixJsonShape.FromShell</c> then turns the value
/// into one <see cref="JsonElement"/>, deserialized once through the <see cref="JsonTypeInfo{T}"/> the wire uses, so
/// every converter and nested list or record is handled by the contract that (de)serializes the wire.
/// <see cref="LanguagePrimitives.ConvertTo(object,Type)"/> is not used: it matches keys by C# name only
/// (<c>"ipmi_username"</c> does not bind <c>IpmiUsername</c>) and cannot populate an <c>IReadOnlyList&lt;T&gt;</c>
/// property at any depth. Every member is a pure function of its arguments. <see cref="ZabbixRecordConverter"/> wraps
/// <see cref="Bind(object?, JsonTypeInfo)"/>, so <c>[ZabbixHostCreateRequest]@{...}</c> binds through PowerShell's own
/// parameter binder.
/// </remarks>
internal static class ZabbixJsonBinder
{
    // Cached once per type: every public instance property's own wire name (JsonPropertyNameAttribute), keyed
    // case-insensitively — the same lookup the type's own JSON contract uses to read/write, so a hashtable key like
    // "ipmi_username" and the wire agree on what it means.
    private static readonly ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>> s_wirePropertiesByType = new();

    /// <summary>
    /// Converts <paramref name="value"/> to <typeparamref name="T"/> through <paramref name="typeInfo"/> — the
    /// entry point a cmdlet calls (<see cref="ZabbixRecordConverter"/> calls the non-generic form). An already-<typeparamref name="T"/>
    /// argument passes straight through, unchanged (no round trip through JSON at all). A <see cref="Hashtable"/>,
    /// any other <see cref="IDictionary"/>, or a <c>[pscustomobject]</c> has its keys remapped to the wire name first
    /// (<see cref="RemapKeys"/>, recursively); everything else is handed to <c>ZabbixJsonShape.FromShell</c> as given.
    /// </summary>
    public static T Bind<T>(object? value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return (T)Bind(value, (JsonTypeInfo)typeInfo)!;
    }

    /// <summary>
    /// <see cref="Bind{T}"/>'s own non-generic form, for a caller that only has a <see cref="JsonTypeInfo"/> (not the
    /// closed <see cref="JsonTypeInfo{T}"/>) — <see cref="ZabbixRecordConverter"/>, which resolves the target type at
    /// run time from whatever destination type PowerShell's own binder asks for, one shared reflection-built index
    /// rather than a generic method per closed type.
    /// </summary>
    internal static object? Bind(object? value, JsonTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        object? unwrapped = Unwrap(value);
        if (typeInfo.Type.IsInstanceOfType(unwrapped)) return unwrapped;
        object? shaped = AsHashtable(unwrapped) is { } table ? RemapKeys(table, typeInfo.Type) : unwrapped;
        JsonElement element = ZabbixJsonShape.FromShell(shaped);
        return element.Deserialize(typeInfo)
            ?? throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"'{value}' does not shape a {typeInfo.Type.Name}"));
    }

    /// <summary>
    /// <paramref name="properties"/> as the JSON object <paramref name="targetType"/>'s own contract reads: its keys
    /// remapped to wire names (<see cref="RemapKeys"/>), the values written by <c>ZabbixJsonShape.FromShell</c>. The
    /// <c>-AdditionalProperty</c> half of <see cref="ZabbixPropertyOverrides.Apply{TRequest}"/>.
    /// </summary>
    /// <exception cref="ArgumentException">See <see cref="RemapKeys"/>.</exception>
    internal static JsonObject ToJsonObject(Hashtable properties, Type targetType)
    {
        JsonElement element = ZabbixJsonShape.FromShell(RemapKeys(properties, targetType));
        return element.ValueKind == JsonValueKind.Object ? JsonObject.Create(element)! : [];
    }

    /// <summary>
    /// PowerShell's own "this is really the wrapped value, not an object with members" unwrap: every value here that
    /// arrives from the pipeline or a parameter may be a <see cref="PSObject"/>, but a <see cref="PSObject"/> whose
    /// <see cref="PSObject.BaseObject"/> is the <see cref="PSCustomObject"/> marker holds its real data in the
    /// <see cref="PSObject"/>'s own members (<c>[pscustomobject]@{...}</c>) — unwrapping THAT would lose them, so it
    /// is left wrapped for <see cref="ZabbixJsonShape.FromShell"/> (the only place that knows how to read a
    /// <see cref="PSObject"/>'s members) to shape directly.
    /// </summary>
    internal static object? Unwrap(object? value) => value is PSObject { BaseObject: not PSCustomObject } wrapped ? wrapped.BaseObject : value;

    /// <summary>
    /// Every key of <paramref name="properties"/>, renamed from its C# property name (case-insensitive) or its own
    /// wire name to the wire name <paramref name="targetType"/> declares for it — recursively: a value that is
    /// itself a <see cref="Hashtable"/> (or a list of them) destined for a nested record/record-list property gets
    /// ITS OWN keys remapped against the nested type first, at any depth. A key naming no property of
    /// <paramref name="targetType"/>: on a <c>*GetParameters</c> record, a field of the entity itself goes into
    /// <c>filter</c> (<c>-Filter @{ host = 'web-01' }</c> sends <c>filter.host</c>); on a record with an
    /// extension-data bag it passes through unchanged (the bag, at deserialize time); anything else is refused. Returns
    /// a <see cref="Hashtable"/>, not a <see cref="JsonNode"/>: the remapped structure still goes through
    /// <c>ZabbixJsonShape.FromShell</c> once, at the top, for its enum/number/<c>PSCustomObject</c> shaping.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Two distinct keys in <paramref name="properties"/> resolve to the same wire field with different values — an
    /// ambiguous call (e.g. <c>@{ ipmi_username = 'a'; IpmiUsername = 'b' }</c>), refused rather than silently
    /// picking one. Or a key names nothing <paramref name="targetType"/> (or, for a get, its entity) knows — refused,
    /// naming the key, rather than dropped by the JSON contract or ignored by the server.
    /// </exception>
    public static Hashtable RemapKeys(Hashtable properties, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(targetType);
        var result = new Hashtable(StringComparer.Ordinal);
        var originalKeyOf = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, PropertyInfo> wireProperties = WireMap(targetType);
        Hashtable? folded = null;
        foreach (DictionaryEntry entry in properties)
        {
            string key = entry.Key switch
            {
                string s => s,
                null => throw new ArgumentException("an -AdditionalProperty/-Filter key cannot be null", nameof(properties)),
                _ => entry.Key.ToString() ?? throw new ArgumentException("an -AdditionalProperty/-Filter key cannot be null", nameof(properties)),
            };
            PropertyInfo? property = targetType.GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null) wireProperties.TryGetValue(key, out property);
            bool routed = property is not null && !string.Equals(property.Name, "AdditionalFields", StringComparison.Ordinal);
            if (!routed && UnknownKey(targetType, key) is { } filterField)
            {
                folded ??= new Hashtable(StringComparer.Ordinal);
                folded[filterField] = Unwrap(entry.Value);
                continue;
            }
            string wireKey = routed ? (WireName(property!) ?? property!.Name) : key;
            object? value = routed ? RemapValue(entry.Value, property!.PropertyType) : entry.Value;
            if (result.ContainsKey(wireKey))
            {
                if (!ValuesEqual(result[wireKey], value))
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                        $"'{originalKeyOf[wireKey]}' and '{key}' both resolve to wire field '{wireKey}' with different values — give it once"));
                continue;
            }
            result[wireKey] = value;
            originalKeyOf[wireKey] = key;
        }
        if (folded is not null)
        {
            // -Filter @{ host = 'web-01' } is the API's filter object. Merged with an explicit filter entry the caller
            // also gave (-Filter @{ filter = @{...}; host = '...' }), the explicit one winning a key both name.
            var filter = new Hashtable(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in folded) filter[entry.Key] = entry.Value;
            if (result["filter"] is object explicitFilter && AsHashtable(Unwrap(explicitFilter)) is { } given)
                foreach (DictionaryEntry entry in given) filter[entry.Key] = entry.Value;
            result["filter"] = filter;
        }
        return result;
    }

    // A key naming no member of targetType. On a *GetParameters record, a key naming a field of the entity itself
    // (ZabbixHostGetParameters → ZabbixHost's own wire fields) belongs in the API's filter object — returns that
    // field's wire name. On a record that carries an extension-data bag it is sent as given (null: the caller's escape hatch for a field a newer Zabbix added).
    // Anything else would be dropped by the JSON contract, or (a get's bag) sent as a top-level parameter the server
    // ignores without an error (host.get on 7.0.31 and 8.0.0: {"host":"zzz"} returns every host) — so it is refused,
    // naming the key.
    private static string? UnknownKey(Type targetType, string key)
    {
        bool isGet = typeof(global::Community.Zabbix.IZabbixGetParameters).IsAssignableFrom(targetType);
        if (isGet)
        {
            if (ReadRecordOf(targetType) is { } read)
            {
                PropertyInfo? field = read.GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (field is null) WireMap(read).TryGetValue(key, out field);
                if (field is not null && WireName(field) is { } wire) return wire;
            }
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"'{key}' is neither a parameter of {targetType.Name} nor a field of {ReadRecordOf(targetType)?.Name ?? "the entity"} it can filter on"));
        }
        if (targetType.GetProperty("AdditionalFields", BindingFlags.Public | BindingFlags.Instance) is not null) return null;
        throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
            $"'{key}' is not a field of {targetType.Name}"));
    }

    // The entity a get's parameters record selects, from this module's own Get- cmdlets: every one of them is a
    // ZabbixGetCmdlet<TEntity, TParameters>, so the pair is stated once, in the type system, not by a name
    // convention (the overlay's model names diverge from the stems: correlation.get reads ZabbixCorrelationRecord).
    private static readonly Lazy<FrozenDictionary<Type, Type>> s_entityByParameters = new(static () => typeof(ZabbixJsonBinder).Assembly
        .GetTypes()
        .Select(static type =>
        {
            for (Type? t = type.BaseType; t is not null; t = t.BaseType)
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ZabbixGetCmdlet<,>)) return t.GetGenericArguments();
            return null;
        })
        .OfType<Type[]>()
        .DistinctBy(static pair => pair[1])
        .ToFrozenDictionary(static pair => pair[1], static pair => pair[0]));

    private static Type? ReadRecordOf(Type parametersType) => s_entityByParameters.Value.GetValueOrDefault(parametersType);

    // Structural equality over RemapValue's own output shapes (Hashtable/List<object?>/scalar) — NOT
    // ZabbixJsonShape.FromShell (a bare string there means "this is already JSON text to parse", not "this is a
    // string VALUE"; calling it on a plain scalar like "root" throws). Used only to tell whether two -AdditionalProperty
    // keys resolving to the same wire field genuinely conflict, or just happen to repeat the same value.
    private static bool ValuesEqual(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is Hashtable ha && b is Hashtable hb)
        {
            if (ha.Count != hb.Count) return false;
            foreach (DictionaryEntry entry in ha)
                if (!hb.ContainsKey(entry.Key!) || !ValuesEqual(entry.Value, hb[entry.Key!])) return false;
            return true;
        }
        if (a is List<object?> la && b is List<object?> lb)
        {
            if (la.Count != lb.Count) return false;
            for (int i = 0; i < la.Count; i++) if (!ValuesEqual(la[i], lb[i])) return false;
            return true;
        }
        return Equals(a, b);
    }

    // A value bound for a nested record/record-list property: a Hashtable (or a list of them) gets its own keys
    // remapped via RemapKeys, recursively; everything else (a scalar, an enum, a string, an already-typed CLR
    // instance) is left as-is — ZabbixJsonShape.FromShell's own walk handles it at the top, once, the same way it
    // would a value that needed no remapping at all.
    private static object? RemapValue(object? value, Type targetType)
    {
        // A filter's or search's keys are the API's own field names: the wire keeps them as given.
        if (typeof(global::Community.Zabbix.ZabbixGetConditions).IsAssignableFrom(targetType)) return value;
        object? unwrapped = Unwrap(value);
        if (unwrapped is null) return null;
        // One value where the record models a list is the list of that value, as PowerShell promotes a scalar for a T[]
        // parameter and the API itself accepts either (-Filter @{ itemids = $item.ItemId } binds rather than failing with
        // "could not be converted to IReadOnlyList`1[String]").
        if (ListElementType(targetType) is { } listElement && (unwrapped is string || unwrapped is not IEnumerable || AsHashtable(unwrapped) is not null))
            return new List<object?> { RemapValue(unwrapped, listElement) };
        Type elementType = ListElementType(targetType) ?? targetType;
        if (elementType.IsClass && elementType != typeof(string) && AsHashtable(unwrapped) is { } table)
            return RemapKeys(table, elementType);
        // An already-typed SDK record VALUE nested inside a hashtable ([ImportRuleRequest]@{ Hosts =
        // [ImportRuleHostRequest]@{...} }) stays the record: ZabbixJsonShape.FromShell writes it through its OWN
        // source-generated contract (wire names, converters), not its C# property names.
        if (ZabbixContracts.TryFind(unwrapped.GetType(), out JsonTypeInfo? contract) && contract.Kind == JsonTypeInfoKind.Object)
            return unwrapped;
        if (unwrapped is not string && unwrapped is IEnumerable many && ListElementType(targetType) is not null)
        {
            var list = new List<object?>();
            foreach (object? item in many) list.Add(RemapValue(item, elementType));
            return list;
        }
        // Zabbix's own wire convention: many "numeric" fields (ids, durations, timestamps) are transmitted as JSON
        // strings, not numbers (the PHP API's own type juggling) — modelled here as a plain `string` property. A
        // PowerShell caller naturally types an unquoted number for one of these (-AdditionalProperty @{ period = 3600 }),
        // which ZabbixJsonShape.FromShell would otherwise write as a JSON
        // number — strict deserialization to `string` then fails ("Cannot get the value of a token type 'Number' as
        // a string"). Stringify here, where the field's DECLARED type is known, rather than guessing at
        // every numeric-looking value regardless of its destination.
        if (elementType == typeof(string) && unwrapped is not string && IsJsonScalarNumberOrBool(unwrapped))
            return Convert.ToString(unwrapped, CultureInfo.InvariantCulture);
        return unwrapped;
    }

    // Every record-shaped loose value, as the Hashtable RemapKeys walks: a Hashtable as is, any other IDictionary
    // ([ordered]@{...}) copied into one, and a [pscustomobject] flattened from its own properties — which
    // LanguagePrimitives.ConvertTo(pscustomobject, typeof(Hashtable)) does NOT do (PSInvalidCastException:
    // the IDictionary→Hashtable conversion only fires when the source already IS an IDictionary). null for anything
    // else (a scalar, an already-typed record, a list).
    private static Hashtable? AsHashtable(object? value)
    {
        switch (value)
        {
            case Hashtable table:
                return table;
            case IDictionary dictionary:
                return new Hashtable(dictionary);
            case PSObject { BaseObject: PSCustomObject } custom:
                var flattened = new Hashtable();
                foreach (PSPropertyInfo property in custom.Properties) flattened[property.Name] = property.Value;
                return flattened;
            default:
                return null;
        }
    }

    private static bool IsJsonScalarNumberOrBool(object value)
        => value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    // The element type of a generic list-shaped interface a generated request models a nested-record-list field as
    // (ZabbixApiEmitter only ever emits IReadOnlyList<T> for these, but IList/ICollection/IEnumerable<T> are handled
    // too), OR a concrete T[]/List<T> (ZabbixRecordConverter's own use: a cmdlet or record property may declare a
    // record-list parameter either way). null for anything else (a scalar field, a single nested record).
    internal static Type? ListElementType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        if (!type.IsGenericType) return null;
        Type definition = type.GetGenericTypeDefinition();
        if (definition != typeof(IReadOnlyList<>) && definition != typeof(IReadOnlyCollection<>)
            && definition != typeof(IList<>) && definition != typeof(ICollection<>) && definition != typeof(IEnumerable<>)
            && definition != typeof(List<>))
            return null;
        return type.GetGenericArguments()[0];
    }

    // The public properties by wire name, then the wire members the contract includes that no public property names: a
    // get's sortorder, which its public Sort sends with sortfield.
    private static Dictionary<string, PropertyInfo> WireMap(Type t) => s_wirePropertiesByType.GetOrAdd(t, static type =>
    {
        Dictionary<string, PropertyInfo> map = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Property: p, Wire: p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name))
            .Where(p => p.Wire is not null)
            .ToDictionary(p => p.Wire!, p => p.Property, StringComparer.OrdinalIgnoreCase);
        foreach (PropertyInfo included in type.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance).Where(p => p.GetCustomAttribute<JsonIncludeAttribute>() is not null))
            if (WireName(included) is { } wire) map.TryAdd(wire, included);
        return map;
    });

    private static string? WireName(PropertyInfo property) => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
}
