// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Proxy;

/// <summary>Every proxy's version must be a Zabbix version, its offline buffer in upstream's range, and its cadences positive.</summary>
internal sealed class ZabbixProxyProtocolValidator : IValidateOptions<ZabbixProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, ZabbixProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.Version is not null && !ZabbixComponentVersion.TryParse(options.Version, System.Globalization.CultureInfo.InvariantCulture, out _)) failures.Add($"Version '{options.Version}' is not a Zabbix version (major.minor[.patch], e.g. 7.0.31)");
        if (options.OfflineBuffer < TimeSpan.FromHours(1) || options.OfflineBuffer > TimeSpan.FromHours(720)) failures.Add($"OfflineBuffer {options.OfflineBuffer} is outside ProxyOfflineBuffer's 1 to 720 hours");
        if (options.ConfigInterval <= TimeSpan.Zero) failures.Add("ConfigInterval must be positive");
        if (options.DataInterval <= TimeSpan.Zero) failures.Add("DataInterval must be positive");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
