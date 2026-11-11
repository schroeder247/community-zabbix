// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>Which of a machine's names to read.</summary>
public enum ZabbixHostNameKind
{
    /// <summary>Whichever the key answers when not told: the NetBIOS name on Windows, the host name elsewhere.</summary>
    Default,

    /// <summary>The name the system calls itself.</summary>
    HostName,

    /// <summary>The name up to its first dot.</summary>
    ShortHostName,

    /// <summary>The name with its domain, from a name lookup.</summary>
    FullyQualifiedName,

    /// <summary>The NetBIOS name, which only Windows has.</summary>
    NetBiosName,
}
