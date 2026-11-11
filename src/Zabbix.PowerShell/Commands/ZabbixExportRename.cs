// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json.Nodes;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// Renames the one host or template a <c>configuration.export</c> document holds, through the document's structure
/// rather than its text: the object's own technical and visible name, every reference to it (a graph item's or a
/// widget's <c>host</c>, a trigger expression's <c>/name/key</c>), and, for a template, every UUID (the import matches a
/// template and its parts by UUID before name, so a copy that kept them would be taken for the source). A name is a
/// JSON string value, so a quote or a backslash in it is the serializer's to escape.
/// </summary>
internal static class ZabbixExportRename
{
    /// <summary>The document with its one <paramref name="collection"/> object renamed to <paramref name="newName"/>.</summary>
    /// <param name="document">The <c>configuration.export</c> answer (JSON).</param>
    /// <param name="collection"><c>hosts</c> or <c>templates</c>.</param>
    /// <param name="nameMember">The object's technical-name member: <c>host</c> or <c>template</c>.</param>
    /// <param name="oldName">The object's technical name.</param>
    /// <param name="newName">The copy's technical and visible name.</param>
    /// <exception cref="InvalidDataException">The document holds no single object of <paramref name="collection"/>.</exception>
    public static string Rename(string document, string collection, string nameMember, string oldName, string newName)
    {
        JsonObject export = (JsonNode.Parse(document) as JsonObject)?["zabbix_export"] as JsonObject
            ?? throw new InvalidDataException("configuration.export answered no zabbix_export document");
        JsonObject target = (export[collection] as JsonArray)?.OfType<JsonObject>().SingleOrDefault()
            ?? throw new InvalidDataException($"configuration.export answered no single {collection[..^1]} named '{oldName}'");
        bool renewIds = nameMember == "template";
        foreach ((string member, JsonNode? value) in export.ToArray())
        {
            if (member is "version" or "host_groups" or "template_groups") continue;
            Retarget(value, oldName, newName, renewIds);
        }
        target[nameMember] = newName;
        target["name"] = newName;
        return export.Root.ToJsonString();
    }

    private static void Retarget(JsonNode? node, string oldName, string newName, bool renewIds)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (JsonNode? item in array) Retarget(item, oldName, newName, renewIds);
                break;
            case JsonObject value:
                foreach ((string member, JsonNode? child) in value.ToArray())
                {
                    if (child is JsonValue scalar && scalar.TryGetValue(out string? text))
                    {
                        if (member == "host" && text == oldName) value[member] = newName;
                        else if (member == "uuid" && renewIds) value[member] = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                        // A trigger expression names an item's host as "/<technical name>/<key>".
                        else if (member is "expression" or "recovery_expression") value[member] = text.Replace($"/{oldName}/", $"/{newName}/", StringComparison.Ordinal);
                    }
                    else
                    {
                        Retarget(child, oldName, newName, renewIds);
                    }
                }
                break;
        }
    }
}
