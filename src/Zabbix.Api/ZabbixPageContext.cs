// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Specifies whether an item, trigger, graph, discovery rule or web scenario is on a host or on a template: the
/// frontend's <c>context</c> argument, which its forms require and check against the object.
/// </summary>
public enum ZabbixPageContext
{
    /// <summary>On a host (<c>context=host</c>).</summary>
    Host,

    /// <summary>On a template (<c>context=template</c>).</summary>
    Template,
}
