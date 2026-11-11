// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>Which state a process must be in to be counted.</summary>
public enum ZabbixProcessState
{
    /// <summary>Any state.</summary>
    All,

    /// <summary>Running or ready to run.</summary>
    Running,

    /// <summary>Asleep, and able to be woken.</summary>
    Sleeping,

    /// <summary>Finished, and waiting for its parent to read its exit status.</summary>
    Zombie,

    /// <summary>Asleep and unable to be woken, usually waiting for a disk.</summary>
    DiskSleep,

    /// <summary>Stopped or being traced.</summary>
    Traced,
}
