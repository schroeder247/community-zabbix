// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Proxy;

/// <summary>
/// An active proxy's server: one server or the nodes of one HA cluster, as Zabbix's proxy reads its <c>Server</c>
/// (<c>proxy.c</c> <c>zbx_validate_config</c> refuses a comma in it for an active proxy, 7.0/7.4/8.0 alike).
/// </summary>
internal sealed class ZabbixProxyServersValidator : IValidateOptions<ZabbixProxyOptions>
{
    private readonly string? _proxy;

    /// <summary>A validator of every proxy's options: a proxy constructed directly checks its own with it.</summary>
    public ZabbixProxyServersValidator()
    {
    }

    /// <summary>A validator of one active proxy's options, which leaves a passive proxy's in the same container alone.</summary>
    /// <param name="proxy">The active proxy's name.</param>
    public ZabbixProxyServersValidator(string proxy) => _proxy = proxy;

    public ValidateOptionsResult Validate(string? name, ZabbixProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_proxy is not null && _proxy != name) return ValidateOptionsResult.Skip;
        if (string.IsNullOrWhiteSpace(options.Server))
            return ValidateOptionsResult.Fail("Server is required: the server or HA cluster the proxy reports to (WithServer, or a Server entry)");
        if (!ZabbixServerActive.TryParse(options.Server.AsSpan(), CultureInfo.InvariantCulture, out ZabbixServerActive? server, out string? error))
            return ValidateOptionsResult.Fail($"Server \"{options.Server}\" isn't a server: {error}");
        return server.Count == 1
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Server \"{options.Server}\" names {server.Count} servers, and a proxy reports to one server or one HA cluster, as Zabbix's proxy does: "
                + "pass every node of the cluster to one WithServer call, or join them with ';'");
    }
}
