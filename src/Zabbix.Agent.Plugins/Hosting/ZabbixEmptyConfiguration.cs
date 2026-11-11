// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Community.Zabbix.Hosting;

/// <summary>
/// The settings of a plugin that has none: a section with no value and no children, so an entry reads it as it reads a
/// section a file left out, without this package taking the configuration implementation.
/// </summary>
internal sealed class ZabbixEmptyConfiguration(string path) : IConfigurationSection
{
    public string Key => path[(path.LastIndexOf(':') + 1)..];

    public string Path => path;

    public string? Value
    {
        get => null;
        set => throw new NotSupportedException("A plugin without settings has no value to set.");
    }

    public string? this[string key]
    {
        get => null;
        set => throw new NotSupportedException("A plugin without settings has no value to set.");
    }

    public IConfigurationSection GetSection(string key) => new ZabbixEmptyConfiguration(path + ":" + key);

    public IEnumerable<IConfigurationSection> GetChildren() => [];

    public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);
}
