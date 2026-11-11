// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Numerics;
using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Converts JSON at the edge of the shell to and from PowerShell's own shapes: what the API answers untyped (a
/// <see cref="JsonElement"/>: <c>script.execute</c>, <c>sla.getsli</c>, a raw <c>Invoke-ZbxApi</c>) reaches the pipeline
/// shaped as <c>ConvertFrom-Json</c> shapes it (a <c>[pscustomobject]</c> per object, <see cref="object"/>[] per
/// array), and what a user writes (a hashtable, a <c>[pscustomobject]</c>, an array, a typed SDK record, a JSON
/// string) becomes JSON.
/// </summary>
/// <remarks>
/// Both directions are System.Text.Json, so wire JSON never comes from a second JSON stack. PowerShell's
/// <c>Microsoft.PowerShell.Commands.JsonObject</c> (behind <c>ConvertTo-Json</c>/<c>ConvertFrom-Json</c>) is Newtonsoft
/// underneath: it writes a nested SDK record with its C# property names, cannot see a record's converters, and turns
/// an ISO-looking string into a <see cref="DateTime"/> the server never sent. The walk here is what the shell graph
/// needs: an <see cref="IDictionary"/> or a <c>[pscustomobject]</c> is an object, any other <see cref="IEnumerable"/>
/// an array, a type the SDK's JSON contract (<c>ZabbixJson</c>) knows goes through its own contract, and scalars are System.Text.Json's.
/// </remarks>
internal static class ZabbixJsonShape
{
    /// <summary>How deep a user's object is written; <c>ConvertTo-Json</c>'s own ceiling.</summary>
    public const int MaxDepth = 100;

    /// <summary>
    /// <paramref name="element"/> as <c>ConvertFrom-Json</c> shapes it: an object is a <see cref="PSObject"/> with a note
    /// property per member, an array an <see cref="object"/>[], an integer <see cref="long"/> (or
    /// <see cref="BigInteger"/> beyond it), any other number <see cref="double"/>, and so on; an undefined element is
    /// <see langword="null"/>. A string stays the string the server sent (<c>ConvertFrom-Json</c> would turn an
    /// ISO-looking one into a <see cref="DateTime"/>).
    /// </summary>
    /// <param name="element">The JSON.</param>
    /// <exception cref="InvalidDataException">An object carries a member with an empty name, which no PowerShell property can have.</exception>
    public static object? ToShell(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Undefined or JsonValueKind.Null:
                return null;
            case JsonValueKind.Object:
                var shaped = new PSObject();
                foreach (JsonProperty member in element.EnumerateObject())
                {
                    if (member.Name.Length == 0) throw new InvalidDataException("The JSON has a member whose name is empty; a PowerShell property cannot.");
                    object? value = ToShell(member.Value);
                    if (shaped.Properties[member.Name] is { } existing) existing.Value = value;
                    else shaped.Properties.Add(new PSNoteProperty(member.Name, value));
                }
                return shaped;
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(ToShell).ToArray();
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                if (element.TryGetInt64(out long integer)) return integer;
                string text = element.GetRawText();
                return text.AsSpan().IndexOfAny('.', 'e', 'E') < 0
                    ? BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
                    : element.GetDouble();
        }
    }

    /// <summary>
    /// A nested-record builder's output: <paramref name="record"/> written through its
    /// own contract, then shaped as a <see cref="Hashtable"/> keyed by wire name (case-insensitively, PowerShell's own
    /// <c>@{}</c> convention) holding only the <paramref name="bound"/> keys. Every value is what the wire carries, by
    /// construction: a nested object is another such hashtable, an array an <see cref="object"/>[], a scalar as
    /// <see cref="ToShell(JsonElement)"/> gives it (a number <see cref="long"/>, a string the string). So the hashtable binds onto any
    /// owner's record of the same shape, and a raw <c>Invoke-ZbxApi</c> body built from it is valid.
    /// </summary>
    /// <param name="record">The typed record the builder filled.</param>
    /// <param name="contract">Its contract in the SDK's JSON contract (<c>ZabbixJson</c>).</param>
    /// <param name="bound">The wire names of the members the caller bound (<see langword="null"/> entries are skipped).</param>
    internal static Hashtable ToWireTable<T>(T record, JsonTypeInfo<T> contract, IEnumerable<string?> bound)
    {
        JsonElement wire = JsonSerializer.SerializeToElement(record, contract);
        var table = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (string? key in bound)
            if (key is not null && wire.TryGetProperty(key, out JsonElement value)) table[key] = ToWireValue(value);
        return table;
    }

    private static object? ToWireValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var table = new Hashtable(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty member in element.EnumerateObject()) table[member.Name] = ToWireValue(member.Value);
                return table;
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(ToWireValue).ToArray();
            default:
                return ToShell(element);
        }
    }

    /// <summary>
    /// <paramref name="value"/> as JSON: a <see cref="JsonElement"/> as is, a string at the top as the JSON text it
    /// holds, anything else walked (<see cref="ToNode"/>).
    /// </summary>
    /// <param name="value">What the user gave.</param>
    /// <exception cref="JsonException">A string that is not JSON.</exception>
    /// <exception cref="ArgumentException">The value holds a <see cref="SecureString"/>, or nests deeper than <see cref="MaxDepth"/>.</exception>
    public static JsonElement FromShell(object? value)
    {
        object? input = Unwrap(value);
        switch (input)
        {
            case JsonElement element:
                return element.Clone();
            case string text:
                using (JsonDocument document = JsonDocument.Parse(text)) return document.RootElement.Clone();
            default:
                return FromValue(input);
        }
    }

    /// <summary>
    /// <paramref name="value"/> as a JSON value, a string as the string it is (never parsed): for a parameter that takes
    /// data, not JSON text (<c>Send-ZbxHistory -Value</c>). Anything else is walked
    /// (<see cref="ToNode"/>).
    /// </summary>
    /// <param name="value">What the user gave.</param>
    /// <exception cref="ArgumentException">The value holds a <see cref="SecureString"/>, or nests deeper than <see cref="MaxDepth"/>.</exception>
    public static JsonElement FromValue(object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (ToNode(value) is { } node) node.WriteTo(writer);
            else writer.WriteNullValue();
        }
        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// <paramref name="value"/> as a <see cref="JsonNode"/> (<see langword="null"/> for JSON null): an
    /// <see cref="IDictionary"/> or a <c>[pscustomobject]</c> is an object keyed as given, any other
    /// <see cref="IEnumerable"/> (not a string) an array, a type the SDK's JSON contract knows its own contract's
    /// JSON (wire names, converters), an enum its number, a <see cref="DateTimeOffset"/>/<see cref="DateTime"/> Unix
    /// seconds (Zabbix's time), and a scalar itself. A <see cref="SecureString"/> is refused: it is never written.
    /// </summary>
    /// <param name="value">The shell value.</param>
    /// <exception cref="ArgumentException">The value holds a <see cref="SecureString"/>, or nests deeper than <see cref="MaxDepth"/>.</exception>
    public static JsonNode? ToNode(object? value) => Walk(value, 0);

    private static JsonNode? Walk(object? value, int depth)
    {
        if (depth > MaxDepth) throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"the value nests deeper than {MaxDepth} levels"), nameof(value));
        object? input = Unwrap(value);
        switch (input)
        {
            case null:
                return null;
            case JsonNode node:
                return node.DeepClone();
            case JsonElement element:
                return JsonNode.Parse(element.GetRawText());
            case SecureString:
                throw new ArgumentException("a SecureString is never written as JSON: give the secret to the parameter that takes it", nameof(value));
            case string or char or Guid:
                return JsonValue.Create(Convert.ToString(input, CultureInfo.InvariantCulture));
            case bool flag:
                return JsonValue.Create(flag);
            case DateTimeOffset instant:
                return JsonValue.Create(instant.ToUnixTimeSeconds());
            case DateTime time:
                return JsonValue.Create(new DateTimeOffset(time.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : time).ToUnixTimeSeconds());
            case PSObject { BaseObject: PSCustomObject } custom:
                var fromObject = new JsonObject();
                foreach (PSPropertyInfo property in custom.Properties) fromObject[property.Name] = Walk(property.Value, depth + 1);
                return fromObject;
            case IDictionary dictionary:
                var fromDictionary = new JsonObject();
                foreach (DictionaryEntry entry in dictionary)
                    fromDictionary[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty] = Walk(entry.Value, depth + 1);
                return fromDictionary;
        }
        Type type = input.GetType();
        if (Contract(type) is { } contract) return JsonSerializer.SerializeToNode(input, contract);
        if (type.IsEnum) return JsonValue.Create(Convert.ToInt64(input, CultureInfo.InvariantCulture));
        switch (input)
        {
            case byte or sbyte or short or ushort or int or uint or long:
                return JsonValue.Create(Convert.ToInt64(input, CultureInfo.InvariantCulture));
            case ulong unsigned:
                return JsonValue.Create(unsigned);
            case float or double:
                double real = Convert.ToDouble(input, CultureInfo.InvariantCulture);
                return double.IsFinite(real) ? JsonValue.Create(real) : throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{real} has no JSON form"), nameof(value));
            case decimal number:
                return JsonValue.Create(number);
            case BigInteger big:
                return JsonNode.Parse(big.ToString(CultureInfo.InvariantCulture));
            case IEnumerable many:
                var array = new JsonArray();
                foreach (object? item in many) array.Add(Walk(item, depth + 1));
                return array;
        }
        // Any other .NET object: its public properties, as the shell sees them (ConvertTo-Json's own fallback).
        var fromProperties = new JsonObject();
        foreach (PSPropertyInfo property in PSObject.AsPSObject(input).Properties.Where(p => p.IsGettable))
            fromProperties[property.Name] = Walk(property.Value, depth + 1);
        return fromProperties;
    }

    // A type the SDK's one source-generated contract knows (a request, a record, ZabbixOutput, an SDK enum with its own
    // converter...), except the JSON-native shapes the walk above already writes (string, the numbers, collections).
    private static JsonTypeInfo? Contract(Type type)
        => type.Namespace is "Community.Zabbix" && ZabbixContracts.TryFind(type, out JsonTypeInfo? contract) ? contract : null;

    // A PSObject wrapping anything but the [pscustomobject] marker is its base object (PowerShell's own unwrap); the
    // marker's data lives in the PSObject's own members, so that one stays wrapped.
    private static object? Unwrap(object? value) => value is PSObject { BaseObject: not PSCustomObject } wrapped ? wrapped.BaseObject : value;
}
