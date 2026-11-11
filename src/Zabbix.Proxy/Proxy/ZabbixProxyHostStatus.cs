// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Proxy;

/// <summary>A host's status in the server's configuration (upstream's <c>HOST_STATUS_*</c>).</summary>
public enum ZabbixProxyHostStatus
{
    /// <summary><c>HOST_STATUS_MONITORED</c>.</summary>
    Monitored = 0,

    /// <summary><c>HOST_STATUS_NOT_MONITORED</c>: values for its items are refused and <c>active checks</c> answers "not monitored".</summary>
    NotMonitored = 1,
}
