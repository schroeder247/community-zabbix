// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>Which counter of a Windows process to read.</summary>
public enum ZabbixProcessInfoCounter
{
    /// <summary>The memory committed to it, in kilobytes.</summary>
    VirtualSizeKilobytes,

    /// <summary>Its working set, in kilobytes.</summary>
    WorkingSetKilobytes,

    /// <summary>How many page faults it has had.</summary>
    PageFaults,

    /// <summary>Its time running kernel code, in milliseconds.</summary>
    KernelTimeMilliseconds,

    /// <summary>Its time running its own code, in milliseconds.</summary>
    UserTimeMilliseconds,

    /// <summary>How many GDI objects it holds.</summary>
    GdiObjects,

    /// <summary>How many USER objects it holds.</summary>
    UserObjects,

    /// <summary>The bytes it has read.</summary>
    IoReadBytes,

    /// <summary>The read operations it has done.</summary>
    IoReadOperations,

    /// <summary>The bytes it has written.</summary>
    IoWriteBytes,

    /// <summary>The write operations it has done.</summary>
    IoWriteOperations,

    /// <summary>The bytes it has moved in operations other than reads and writes.</summary>
    IoOtherBytes,

    /// <summary>The operations it has done other than reads and writes.</summary>
    IoOtherOperations,
}
