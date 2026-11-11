// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Configuration;

namespace Community.Zabbix;

/// <summary>
/// Which plugins an agent loads, and from where: Zabbix's <c>LoadModulePath</c> and <c>LoadModule</c> lines, or agent
/// 2's <c>Plugins.&lt;Name&gt;.System.Path</c>. Only the plugins <see cref="Load"/> names are loaded; a file in the
/// folder that nothing lists is never opened.
/// </summary>
public sealed class ZabbixPluginOptions
{
    /// <summary>
    /// Gets or sets the folder the plugins are in: one subfolder per plugin, <c>&lt;Path&gt;/&lt;Name&gt;/</c>, holding
    /// the output of <c>dotnet publish</c> for the plugin's project, its assembly <c>&lt;Name&gt;.dll</c> among it.
    /// Required when <see cref="Load"/> names a plugin.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>Gets the plugins to load, by name, in order.</summary>
    public IList<string> Load { get; } = [];

    /// <summary>
    /// Gets or sets where each plugin's settings are: the section named after the plugin is what its entry receives
    /// (<c>Settings:SqlServer</c>); <see langword="null"/> gives every plugin an empty section.
    /// </summary>
    public IConfiguration? Settings { get; set; }

    /// <summary>What a configuration section lists: <c>Path</c>, <c>Load</c> (an array or a comma-separated list) and each plugin's settings.</summary>
    /// <param name="configuration">The section.</param>
    internal static ZabbixPluginOptions From(IConfiguration configuration)
    {
        var options = new ZabbixPluginOptions { Path = configuration["Path"], Settings = configuration };
        IConfigurationSection load = configuration.GetSection("Load");
        IEnumerable<string?> names = load.Value is { } list
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : load.GetChildren().Select(name => name.Value);
        foreach (string? name in names)
            if (name is not null)
                options.Load.Add(name);
        return options;
    }
}
