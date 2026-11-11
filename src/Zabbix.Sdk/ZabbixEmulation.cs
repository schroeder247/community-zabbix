// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Community.Zabbix;

/// <summary>The JSON-level steps the compositions share.</summary>
internal static class ZabbixEmulation
{
    /// <summary>The request as the JSON object the removed method would have been sent.</summary>
    public static JsonObject Object<T>(T request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        => JsonSerializer.SerializeToNode(request, info) as JsonObject ?? throw new ArgumentException("The request does not serialize to a JSON object.", nameof(request));

    /// <summary>The request's fields without the ones that name the targets.</summary>
    public static JsonObject Fields(JsonNode request, params string[] without)
    {
        JsonObject fields = request.AsObject();
        foreach (string name in without) fields.Remove(name);
        return fields;
    }

    /// <summary>One update: the target's id and a copy of the shared fields, its macros given their existing ids.</summary>
    public static JsonObject Update(JsonObject fields, string idField, string id, Dictionary<string, string>? macroIds)
    {
        var update = new JsonObject { [idField] = id };
        foreach ((string name, JsonNode? value) in fields) update[name] = value?.DeepClone();
        if (macroIds is not null && update["macros"] is JsonArray macros)
        {
            foreach (JsonObject macro in macros.OfType<JsonObject>())
            {
                if (macro["macro"]?.GetValue<string>() is { } key && macroIds.TryGetValue(key.Trim(), out string? macroId)) macro["hostmacroid"] = macroId;
            }
        }
        return update;
    }

    /// <summary>The existing macros of each target: its id → (macro name → hostmacroid).</summary>
    public static async Task<Dictionary<string, Dictionary<string, string>>> MacroIdsAsync(IZabbixTransport transport, string method, string idsOption, string idField, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var parameters = new JsonObject { [idsOption] = new JsonArray([.. ids.Select(id => (JsonNode)id)]), ["output"] = new JsonArray(idField), ["selectMacros"] = new JsonArray("hostmacroid", "macro") };
        JsonElement found = await SendAsync(transport, method, parameters, cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (JsonElement target in Rows(found, method))
        {
            var byName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonElement macro in Rows(Member(target, "macros", method), method))
                byName[Text(macro, "macro", method).Trim()] = Text(macro, "hostmacroid", method);
            result[Text(target, idField, method)] = byName;
        }
        return result;
    }

    /// <summary>
    /// hostgroup/templategroup.massupdate {groups: G, hosts: H}: every target in H ends in G (plus its other groups); every
    /// current member of G not in H leaves G. A target that would be left without a group is refused before anything is
    /// sent — the whole method sends nothing until every target's next membership is known to be non-empty. Two reads (the
    /// named targets' groups, then the group's current members) and no write in between: not atomic against a membership
    /// change that lands on the server between the reads and the caller's own write. The refusal is the one the native
    /// method gives (<c>CHostGroup.php</c>, <c>CTemplateGroup.php</c> 7.0): a <see cref="ZabbixApiException"/> of kind
    /// <see cref="ZabbixApiErrorKind.Validation"/>, so a caller handles a 7.0 server and an 8.0 server alike.
    /// </summary>
    public static async Task<JsonArray> ReplaceMembershipAsync(IZabbixTransport transport, string target, string idField, string groupsField,
        IReadOnlyList<string> groups, IReadOnlyList<string> members, CancellationToken cancellationToken)
    {
        var wanted = new HashSet<string>(groups, StringComparer.Ordinal);
        var keep = new HashSet<string>(members, StringComparer.Ordinal);
        string selectGroups = target == "host" ? "selectHostGroups" : "selectTemplateGroups";
        var current = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonObject query in new[]
        {
            new JsonObject { [idField + "s"] = new JsonArray([.. members.Select(id => (JsonNode)id)]), ["output"] = new JsonArray(idField, "host"), [selectGroups] = new JsonArray("groupid") },
            new JsonObject { ["groupids"] = new JsonArray([.. groups.Select(id => (JsonNode)id)]), ["output"] = new JsonArray(idField, "host"), [selectGroups] = new JsonArray("groupid") },
        })
        {
            if (members.Count == 0 && query.ContainsKey(idField + "s")) continue;
            JsonElement found = await SendAsync(transport, target + ".get", query, cancellationToken).ConfigureAwait(false);
            string method = target + ".get";
            foreach (JsonElement item in Rows(found, method))
            {
                string id = Text(item, idField, method);
                current[id] = [.. Rows(Member(item, groupsField, method), method).Select(g => Text(g, "groupid", method))];
                if (item.TryGetProperty("host", out JsonElement name) && name.GetString() is { } host) names[id] = host;
            }
        }
        var updates = new JsonArray();
        foreach ((string id, List<string> now) in current)
        {
            List<string> next = keep.Contains(id) ? [.. now.Union(wanted, StringComparer.Ordinal)] : [.. now.Where(g => !wanted.Contains(g))];
            if (next.Count == 0)
            {
                // Upstream's own words and code (ZBX_API_ERROR_PARAMETERS), named by the technical name as upstream names it.
                string noun = target == "host" ? "Host" : "Template";
                throw new ZabbixApiException(-32602, "Invalid params.", $"{noun} \"{names.GetValueOrDefault(id, id)}\" cannot be without {target} group.", groupsField[..^1] + ".massupdate");
            }
            if (next.Count == now.Count && next.All(now.Contains)) continue;
            updates.Add((JsonNode)new JsonObject { [idField] = id, ["groups"] = new JsonArray([.. next.Select(g => (JsonNode)new JsonObject { ["groupid"] = g })]) });
        }
        return updates;
    }

    /// <summary>Sends <paramref name="parameters"/> as they are: the node is written straight into the request.</summary>
    public static Task<JsonElement> SendAsync(IZabbixTransport transport, string method, JsonNode parameters, CancellationToken cancellationToken)
        => transport.SendAsync(method, parameters, ZabbixApiJson.Default.JsonNode, cancellationToken);

    /// <summary>The rows of a <c>.get</c> reply, or of a list member of one; a reply of another shape is not the method's.</summary>
    /// <exception cref="InvalidDataException">The element is not an array.</exception>
    public static JsonElement.ArrayEnumerator Rows(JsonElement element, string method)
        => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : throw Malformed(method, $"holds a {element.ValueKind} where a list was asked for");

    /// <summary>The text of <paramref name="member"/>, which the request asked for.</summary>
    /// <exception cref="InvalidDataException">The row has no such member, or it is not text.</exception>
    public static string Text(JsonElement row, string member, string method)
        => Member(row, member, method) is { ValueKind: JsonValueKind.String } value && value.GetString() is { } text
            ? text
            : throw Malformed(method, $"carries a '{member}' that is not text");

    /// <summary>The id in <paramref name="member"/>, which the request asked for.</summary>
    /// <exception cref="InvalidDataException">The row has no such member, or it is not an id.</exception>
    public static ulong Id(JsonElement row, string member, string method)
        => Member(row, member, method) is { ValueKind: JsonValueKind.String } value && ulong.TryParse(value.GetString(), null, out ulong id)
            ? id
            : throw Malformed(method, $"carries a '{member}' that is not an id");

    private static JsonElement Member(JsonElement row, string member, string method)
        => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(member, out JsonElement value)
            ? value
            : throw Malformed(method, $"carries a row without the '{member}' it was asked for");

    // A reply that is not the method's shape is the server's fault and reads as such (invalid_response), never as a
    // KeyNotFoundException: that is ZabbixNotFoundException's base, and a malformed reply is not "not found".
    private static InvalidDataException Malformed(string method, string why) => new($"The {method} reply {why}.");
}
