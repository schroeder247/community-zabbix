// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Globalization;
using System.Management.Automation;
using Microsoft.Extensions.Configuration;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// The parameters both agent starters take: the session's name, the host name, and the TLS words of
/// <see cref="ZabbixTlsCmdlet"/>; the active agent adds <c>-Tls</c> (Zabbix's <c>TLSConnect</c>), the passive agent
/// <c>-TlsAccept</c>.
/// </summary>
public abstract class ZabbixAgentSessionCommand : ZabbixTlsCmdlet
{
    /// <summary>The name this session is kept under; unique among the runspace's agent sessions, active and passive alike.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrWhiteSpace]
    public string Name { get; set; } = null!;

    /// <summary>What <c>agent.hostname</c> answers, and (for an active agent) the host the server assigns items to.</summary>
    [Parameter(Position = 1)]
    [ValidateNotNullOrWhiteSpace]
    public string HostName { get; set; } = Environment.MachineName;

    /// <summary>Writes the session to the pipeline.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <summary>
    /// Which keys the agent answers, as lines of Zabbix's agent configuration, tried in order, the first that matches
    /// deciding: <c>'AllowKey=vfs.file.*[*]', 'DenyKey=*'</c>. In a pattern <c>*</c> matches any characters, in the name
    /// or in a parameter. Without it the agent answers every key but <c>system.run</c>, as Zabbix's agents do. A denied
    /// key is answered as one nobody answers.
    /// </summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    public string[]? KeyRule { get; set; }

    /// <summary>
    /// Leaves out the bundled items, Zabbix's own system, process, file, network and web keys: the session answers the
    /// four keys every agent answers, its scriptblock handlers and its plugins only.
    /// </summary>
    [Parameter]
    public SwitchParameter NoBundledItems { get; set; }

    // The key rules and the bundled items, as the agent's options take them.
    private protected void ApplyKeys(IList<string> keyRules, Action<bool> bundled)
    {
        foreach (string rule in KeyRule ?? []) keyRules.Add(rule);
        bundled(!NoBundledItems.IsPresent);
    }

    /// <summary>
    /// Plugins to load into the agent, by name: each a folder under <c>-PluginPath</c> holding a plugin's
    /// <c>dotnet publish</c> output, whose item keys the agent then answers beside the built-in keys and the session's
    /// scriptblock handlers. A plugin that cannot be loaded fails the start, and a plugin whose files other accounts can
    /// write is refused on Linux and macOS.
    /// </summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    public string[]? Plugin { get; set; }

    /// <summary>The folder the plugins <c>-Plugin</c> names are in.</summary>
    [Parameter]
    [ValidateNotNullOrWhiteSpace]
    public string? PluginPath { get; set; }

    /// <summary>
    /// Each plugin's settings, under its name: <c>@{ SqlServer = @{ ConnectionString = '…' } }</c>. A nested hashtable
    /// is a configuration section, as a JSON object is.
    /// </summary>
    [Parameter]
    public Hashtable? PluginSetting { get; set; }

    // The plugins -Plugin lists, as an application's AddPlugins takes them; null when none is listed. Writes the error
    // and answers false when -Plugin has nowhere to load from.
    private protected bool TryPluginOptions(out ZabbixPluginOptions? plugins)
    {
        plugins = null;
        if (Plugin is null) return true;
        if (PluginPath is null)
        {
            WriteError(new ErrorRecord(new ArgumentException("-Plugin needs -PluginPath, the folder the plugins are in."),
                "ZabbixPluginPathMissing", ErrorCategory.InvalidArgument, Plugin));
            return false;
        }
        plugins = new ZabbixPluginOptions
        {
            Path = SessionState.Path.GetUnresolvedProviderPathFromPSPath(PluginPath),
            Settings = new ConfigurationBuilder().AddInMemoryCollection(Flatten(PluginSetting, prefix: null)).Build(),
        };
        foreach (string name in Plugin) plugins.Load.Add(name);
        return true;
    }

    // A hashtable as configuration keys: nested hashtables are sections (SqlServer:ConnectionString), a list's items
    // are numbered (Load:0), and a value is its invariant text.
    private static IEnumerable<KeyValuePair<string, string?>> Flatten(IDictionary? table, string? prefix)
    {
        if (table is null) yield break;
        foreach (DictionaryEntry entry in table)
        {
            string key = prefix is null ? Convert.ToString(entry.Key, CultureInfo.InvariantCulture)! : prefix + ":" + Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
            object? value = entry.Value is PSObject wrapped ? wrapped.BaseObject : entry.Value;
            if (value is IDictionary section)
            {
                foreach (KeyValuePair<string, string?> nested in Flatten(section, key)) yield return nested;
            }
            else if (value is IEnumerable list and not string)
            {
                int index = 0;
                foreach (object? item in list)
                    yield return new(key + ":" + (index++).ToString(CultureInfo.InvariantCulture), Convert.ToString(item is PSObject p ? p.BaseObject : item, CultureInfo.InvariantCulture));
            }
            else
            {
                yield return new(key, Convert.ToString(value, CultureInfo.InvariantCulture));
            }
        }
    }

    private protected bool NameInUse<TSession>() where TSession : class, IZabbixSession
    {
        if (!ZabbixContext.Sessions.TryGet(Name, out TSession? _)) return false;
        WriteError(new ErrorRecord(new InvalidOperationException($"an agent session named '{Name}' is already running"), "ZabbixAgentNameInUse", ErrorCategory.ResourceExists, Name));
        return true;
    }
}
