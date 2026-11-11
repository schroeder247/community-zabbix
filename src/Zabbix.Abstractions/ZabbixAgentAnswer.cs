// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

namespace Community.Zabbix;

/// <summary>What a passive agent answered for a key: the value, or that the key is not supported and why.</summary>
/// <param name="Key">The key asked.</param>
/// <param name="Value">The value; <see langword="null"/> when not supported, or when a supported key had no value this time (the agent answered <c>null</c>).</param>
/// <param name="Error">Why the key is not supported; <see langword="null"/> when it is.</param>
public sealed record ZabbixAgentAnswer(ZabbixItemKey Key, string? Value, string? Error)
{
    /// <summary>Whether the agent supports the key.</summary>
    public bool IsSupported => Error is null;

    /// <summary>Creates the answer of a supported key.</summary>
    /// <param name="key">The key asked.</param>
    /// <param name="value">The value; <see langword="null"/> when the key had no value this time.</param>
    /// <returns>An answer whose <see cref="IsSupported"/> is <see langword="true"/>.</returns>
    public static ZabbixAgentAnswer Supported(ZabbixItemKey key, string? value) => new(key, value, null);

    /// <summary>Creates the answer of a key the agent does not support.</summary>
    /// <param name="key">The key asked.</param>
    /// <param name="error">Why the key is not supported.</param>
    /// <returns>An answer whose <see cref="IsSupported"/> is <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static ZabbixAgentAnswer NotSupported(ZabbixItemKey key, string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(key, null, error);
    }
}
