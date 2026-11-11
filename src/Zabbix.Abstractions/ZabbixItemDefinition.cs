// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

namespace Community.Zabbix;

/// <summary>
/// Represents one key a <see cref="ZabbixItemProvider"/> answers: its name, what it measures, and a key to try it with,
/// as Zabbix's loadable modules list their keys (<c>ZBX_METRIC</c>: the key, and a test parameter).
/// </summary>
public sealed record ZabbixItemDefinition
{
    /// <summary>Initializes a definition.</summary>
    /// <param name="keyName">The key's name, without parameters (<c>app.queue.depth</c>).</param>
    /// <param name="description">What the key measures, for a reader and a listing.</param>
    /// <exception cref="ArgumentException"><paramref name="keyName"/> is not a key name, or <paramref name="description"/> is empty.</exception>
    public ZabbixItemDefinition(string keyName, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (!ZabbixItemKey.TryParse(keyName, out ZabbixItemKey key) || key.Parameters.Count != 0 || key.Name != keyName)
            throw new ArgumentException($"'{keyName}' is not an item key name: a name has no parameters (app.queue.depth, not app.queue.depth[orders]).", nameof(keyName));
        KeyName = keyName;
        Description = description;
    }

    /// <summary>Gets the key's name, without parameters.</summary>
    public string KeyName { get; }

    /// <summary>Gets what the key measures.</summary>
    public string Description { get; }

    /// <summary>
    /// Gets a whole key to try this one with (<c>app.queue.depth[orders]</c>), as a module's test parameter is;
    /// <see langword="null"/> when the name alone is a key to try.
    /// </summary>
    /// <exception cref="ArgumentException">Setting: the key does not parse, or names another key.</exception>
    public string? TestKey
    {
        get;
        init
        {
            if (value is not null && (!ZabbixItemKey.TryParse(value, out ZabbixItemKey key) || key.Name != KeyName))
                throw new ArgumentException($"The test key '{value}' is not a key named '{KeyName}'.", nameof(value));
            field = value;
        }
    }
}
