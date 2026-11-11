// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>An agent doesn't support an item that was read as a typed value. To get this as an answer, not an exception, read the item by its key.</summary>
public sealed class ZabbixItemNotSupportedException : ZabbixRefusedException
{
    /// <summary>Initializes an exception with no detail.</summary>
    public ZabbixItemNotSupportedException()
    {
    }

    /// <summary>Initializes an exception with a message.</summary>
    /// <param name="message">The message, which says what isn't supported.</param>
    public ZabbixItemNotSupportedException(string message) : base(message)
    {
    }

    /// <summary>Initializes an exception with a message and the failure that caused it.</summary>
    /// <param name="message">The message, which says what isn't supported.</param>
    /// <param name="innerException">The failure that caused this one.</param>
    public ZabbixItemNotSupportedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Initializes the exception for the agent's answer about <paramref name="key"/>.</summary>
    /// <param name="key">The key that was asked for.</param>
    /// <param name="error">The agent's reason, verbatim: the answer's <see cref="ZabbixAgentAnswer.Error"/>.</param>
    public ZabbixItemNotSupportedException(ZabbixItemKey key, string? error)
        : base($"The agent doesn't support this item: {error ?? "it gave no reason"}")
    {
        Key = key;
        Error = error;
    }

    /// <summary>Gets the key that was asked for.</summary>
    /// <value>The key; <see langword="default"/> when the exception was not built from an answer.</value>
    public ZabbixItemKey Key { get; }

    /// <summary>Gets the agent's reason, as <see cref="ZabbixAgentAnswer.Error"/> carries it.</summary>
    /// <value>The reason, verbatim; <see langword="null"/> when the agent gave none.</value>
    public string? Error { get; }
}
