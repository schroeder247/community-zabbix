// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>
/// Requires an update's own id. An update names each object it changes by its id and answers those ids (<c>host.update</c>
/// takes <c>hostid</c> and answers <c>hostids</c>), so a request without its id changes nothing the caller meant. The
/// description marks the id required where the method's input rules name it; <c>graph.update</c> and
/// <c>graphprototype.update</c> read it in code instead, looking each graph up by its <c>graphid</c> and refusing one they
/// can't find (CGraphGeneral.php 8.0:54-87), so the extraction doesn't see it required. It's marked here, once, for every
/// emitter: the request's member is <c>required</c> and its constructor takes it.
/// </summary>
internal static class ZabbixUpdateIds
{
    /// <summary><paramref name="api"/> with the id field of every update's request required.</summary>
    /// <param name="api">The description.</param>
    public static ApiDescription Require(ApiDescription api)
    {
        if (api is null) throw new ArgumentNullException(nameof(api));
        var ids = new HashSet<(string Record, string Wire)>();
        foreach (ServiceDescription service in api.Services)
        {
            foreach (MethodDescription method in service.Methods)
            {
                // the field the answer's ids name, in the request that lists the objects: ids:graphids is graphid
                if (!method.Api.EndsWith(".update", StringComparison.Ordinal) || !method.Result.StartsWith("ids:", StringComparison.Ordinal) || !method.Result.EndsWith("s", StringComparison.Ordinal)) continue;
                string? record = method.Parameters.StartsWith("list:object:", StringComparison.Ordinal) ? method.Parameters.Substring(12)
                    : method.Parameters.StartsWith("object:", StringComparison.Ordinal) ? method.Parameters.Substring(7) : null;
                if (record is not null) ids.Add((record, method.Result.Substring(4, method.Result.Length - 5)));
            }
        }
        bool Unmarked(TypeDescription type, FieldDescription field) => !field.Required && field.Kind == "id" && ids.Contains((type.Name, field.Wire));
        return api.Types.Any(type => type.Fields.Any(field => Unmarked(type, field)))
            ? api with { Types = [.. api.Types.Select(type => type.Fields.Any(field => Unmarked(type, field)) ? type with { Fields = [.. type.Fields.Select(field => Unmarked(type, field) ? field with { Required = true } : field)] } : type)] }
            : api;
    }
}
