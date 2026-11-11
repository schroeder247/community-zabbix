// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>Which UDP sockets to count.</summary>
public enum ZabbixUdpSocketState
{
    /// <summary>Every UDP socket.</summary>
    Any,

    /// <summary>Only sockets connected to a remote end.</summary>
    Established,

    /// <summary>Only sockets with no remote end.</summary>
    Unconnected,
}
