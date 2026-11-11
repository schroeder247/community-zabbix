// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// The member is gone from this Zabbix version on (<see cref="System.Runtime.Versioning.UnsupportedOSPlatformAttribute"/>'s
/// precedent). A generated client method so marked is refused before sending with <see cref="ZabbixVersionException"/>,
/// or composed from the methods the server has where the SDK carries a composition.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class ZabbixUnsupportedAttribute : Attribute
{
    /// <summary>Marks the member as gone from <paramref name="version"/> on, with nothing to use instead.</summary>
    /// <param name="version">The first Zabbix version without the member, <c>major.minor</c>.</param>
    public ZabbixUnsupportedAttribute(string version)
        : this(version, message: null)
    {
    }

    /// <summary>Marks the member as gone from <paramref name="version"/> on.</summary>
    /// <param name="version">The first Zabbix version without the member, <c>major.minor</c>.</param>
    /// <param name="message">What to use instead, when there is something.</param>
    public ZabbixUnsupportedAttribute(string version, string? message)
    {
        Version = version;
        Message = message;
    }

    /// <summary>The first Zabbix version without the member, <c>major.minor</c>.</summary>
    public string Version { get; }

    /// <summary>What to use instead.</summary>
    public string? Message { get; }
}
