// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// The member exists from this Zabbix version on (the platform's <see cref="System.Runtime.Versioning.SupportedOSPlatformAttribute"/>
/// precedent, for server versions). Generated from the Zabbix source; absent on members every described version has.
/// </summary>
/// <param name="version">The first Zabbix version with the member, <c>major.minor</c>.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class ZabbixSupportedAttribute(string version) : Attribute
{
    /// <summary>The first Zabbix version with the member, <c>major.minor</c>.</summary>
    public string Version { get; } = version;
}
