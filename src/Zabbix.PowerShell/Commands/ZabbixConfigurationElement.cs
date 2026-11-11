// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell.Commands;

/// <summary>One element of a list section, as the server wrote it.</summary>
internal sealed class ZabbixConfigurationElement(string text)
{
    /// <summary>The element as the server wrote it.</summary>
    public string Text { get; } = text;
}
