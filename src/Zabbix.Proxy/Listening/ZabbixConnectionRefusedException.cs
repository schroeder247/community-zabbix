// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix.Listening;

/// <summary>A connection refused inside the listener's accept (its protection is not accepted); the reason is Zabbix's own words.</summary>
internal sealed class ZabbixConnectionRefusedException : IOException, IZabbixErrorTyped
{
    public ZabbixConnectionRefusedException()
        : this("The connection was refused.")
    {
    }

    public ZabbixConnectionRefusedException(string message)
        : this(message, ZabbixListenerRejection.ConnectionRefused)
    {
    }

    public ZabbixConnectionRefusedException(string message, Exception innerException)
        : base(message, innerException) => ErrorType = ZabbixListenerRejection.ConnectionRefused;

    public ZabbixConnectionRefusedException(string reason, string errorType)
        : base(reason) => ErrorType = errorType;

    public string ErrorType { get; }
}
