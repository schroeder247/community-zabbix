// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix.Proxy;

/// <summary>What the server answers a <c>proxy data</c> or <c>proxy config</c> with.</summary>
public sealed record ZabbixProxyResponse(
    [property: JsonPropertyName("response")] string? Response,
    [property: JsonPropertyName("info")] string? Info,
    [property: JsonPropertyName("upload")] string? Upload,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("tasks")] JsonElement? Tasks)
{
    /// <summary>Whether the server took it.</summary>
    public bool IsSuccess => string.Equals(Response, "success", StringComparison.Ordinal);

    /// <summary>Whether the server has disabled history uploads (<c>"upload":"disabled"</c>), which it says with a success (to a probe) or a failure (to a page).</summary>
    public bool UploadDisabled => string.Equals(Upload, "disabled", StringComparison.Ordinal);

    /// <summary>How many tasks the server handed over with its acknowledgement.</summary>
    public int TaskCount => Tasks is { ValueKind: JsonValueKind.Array } tasks ? tasks.GetArrayLength() : 0;

    /// <summary>Whether <paramref name="other"/> is the same reply, its tasks by their JSON.</summary>
    /// <param name="other">The other reply.</param>
    public bool Equals(ZabbixProxyResponse? other)
        => other is not null && string.Equals(Response, other.Response, StringComparison.Ordinal) && string.Equals(Info, other.Info, StringComparison.Ordinal)
            && string.Equals(Upload, other.Upload, StringComparison.Ordinal) && string.Equals(Version, other.Version, StringComparison.Ordinal)
            && ZabbixValueEquality.JsonEqual(Tasks, other.Tasks);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Response, StringComparer.Ordinal);
        hash.Add(Info, StringComparer.Ordinal);
        ZabbixValueEquality.AddJson(ref hash, Tasks);
        return hash.ToHashCode();
    }
}
