// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Hosting;

/// <summary>The one check both agents make of their <c>ServerActive</c>.</summary>
internal static class ZabbixServerActiveValidation
{
    public static ValidateOptionsResult Validate(string? serverActive)
    {
        if (string.IsNullOrWhiteSpace(serverActive)) return ValidateOptionsResult.Fail("ServerActive needs at least one node (AddServer, or a ServerActive value such as \"zbx1;zbx2,proxy-b\")");
        return ZabbixServerActive.TryParse(serverActive, CultureInfo.InvariantCulture, out _, out string? error) ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error);
    }

    /// <summary>The options' value, parsed: empty when unset (the validator has refused that already).</summary>
    public static ZabbixServerActive Parse(string? serverActive)
        => string.IsNullOrWhiteSpace(serverActive) ? ZabbixServerActive.Empty : ZabbixServerActive.Parse(serverActive, CultureInfo.InvariantCulture);
}
