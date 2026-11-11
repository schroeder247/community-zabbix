// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>Which of a process's memory figures to read.</summary>
public enum ZabbixProcessMemoryType
{
    /// <summary>The virtual memory it has mapped.</summary>
    VirtualSize,

    /// <summary>What it holds in physical memory now.</summary>
    ResidentSet,

    /// <summary>Its data, stack and code segments together.</summary>
    DataStackAndCode,

    /// <summary>The most virtual memory it has had mapped.</summary>
    PeakVirtualSize,

    /// <summary>What it has swapped out.</summary>
    Swapped,

    /// <summary>Its shared libraries' code.</summary>
    SharedLibraries,

    /// <summary>What it has locked into memory.</summary>
    Locked,

    /// <summary>What it has pinned in memory.</summary>
    Pinned,

    /// <summary>The most it has held in physical memory.</summary>
    PeakResidentSet,

    /// <summary>Its data segment.</summary>
    Data,

    /// <summary>Its stack segment.</summary>
    Stack,

    /// <summary>Its code segment.</summary>
    Code,

    /// <summary>Its page tables.</summary>
    PageTables,
}
