// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Items;

/// <summary>The types Zabbix's file keys tell apart (<c>ZBX_FT_*</c>, <c>common/dir.c</c>).</summary>
[Flags]
internal enum ZabbixFileType
{
    None = 0,
    File = 0x001,
    Directory = 0x002,
    Symlink = 0x004,
    Socket = 0x008,
    Block = 0x010,
    Character = 0x020,
    Fifo = 0x040,
    All = File | Directory | Symlink | Socket | Block | Character | Fifo,
}

/// <summary>What <c>stat</c> says about a path on Linux and macOS, the same on both.</summary>
/// <param name="Type">The kind of file.</param>
/// <param name="Permissions">The mode without its type bits.</param>
/// <param name="User">The owner's numeric id.</param>
/// <param name="Group">The group's numeric id.</param>
/// <param name="Links">The number of hard links.</param>
/// <param name="Device">The device the file is on: with <paramref name="Inode"/>, what tells a hard link's file apart.</param>
/// <param name="Inode">The inode number.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="Blocks">The space it takes, in 512-byte blocks.</param>
/// <param name="Accessed">When it was last read, in seconds since the epoch.</param>
/// <param name="Modified">When its contents last changed.</param>
/// <param name="Changed">When its contents or its inode last changed.</param>
internal readonly record struct ZabbixFileStat(
    ZabbixFileType Type,
    uint Permissions,
    uint User,
    uint Group,
    uint Links,
    ulong Device,
    ulong Inode,
    ulong Size,
    ulong Blocks,
    long Accessed,
    long Modified,
    long Changed);

/// <summary>What <c>statvfs</c> says about a file system's inodes.</summary>
/// <param name="Total"><c>f_files</c>.</param>
/// <param name="Free"><c>f_favail</c>: the inodes an unprivileged process can use.</param>
/// <param name="Used"><c>f_files</c> less <c>f_ffree</c>.</param>
/// <param name="PercentFree">The free share of what an unprivileged process could have.</param>
/// <param name="PercentUsed">The rest.</param>
internal readonly record struct ZabbixInodes(ulong Total, ulong Free, ulong Used, double PercentFree, double PercentUsed);
