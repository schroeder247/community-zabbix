// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>The kinds of file a key can filter on. Windows has only files, directories and symbolic links.</summary>
[Flags]
public enum ZabbixFileTypes
{
    /// <summary>No filter: leave the choice to the key.</summary>
    None = 0,

    /// <summary>A regular file.</summary>
    File = 1 << 0,

    /// <summary>A directory.</summary>
    Directory = 1 << 1,

    /// <summary>A symbolic link.</summary>
    SymbolicLink = 1 << 2,

    /// <summary>A socket.</summary>
    Socket = 1 << 3,

    /// <summary>A block device.</summary>
    BlockDevice = 1 << 4,

    /// <summary>A character device.</summary>
    CharacterDevice = 1 << 5,

    /// <summary>A named pipe.</summary>
    Fifo = 1 << 6,

    /// <summary>A block or character device.</summary>
    Device = BlockDevice | CharacterDevice,

    /// <summary>Every kind of file.</summary>
    All = File | Directory | SymbolicLink | Socket | BlockDevice | CharacterDevice | Fifo,
}
