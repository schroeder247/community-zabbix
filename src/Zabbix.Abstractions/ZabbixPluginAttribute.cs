// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;

namespace Community.Zabbix;

/// <summary>
/// Names a plugin assembly's entry, the one <see cref="ZabbixPluginEntry"/> an agent creates when it loads the plugin:
/// <c>[assembly: ZabbixPlugin(typeof(QueuePlugin))]</c>. An agent reads this attribute and nothing else, so no type of
/// the plugin is inspected or created until its entry registers it.
/// </summary>
/// <param name="entry">The entry type: a <see cref="ZabbixPluginEntry"/> with a public parameterless constructor.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ZabbixPluginAttribute([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type entry) : Attribute
{
    /// <summary>Gets the entry type.</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public Type Entry { get; } = entry;
}
