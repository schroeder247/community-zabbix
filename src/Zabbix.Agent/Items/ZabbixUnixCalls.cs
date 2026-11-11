// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Community.Zabbix.Items;

/// <summary>
/// The C library functions the file keys call on Linux and macOS (docs/adr/0009-bundled-items.md §6). The structures are
/// read by offset from the C headers, so no managed layout can drift from them. On 32-bit Linux <c>statvfs</c>'s fields
/// change width with how a program was compiled, so the keys that need it are not supported there.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static partial class ZabbixUnixCalls
{
    /// <summary><c>ENOENT</c>: the same on Linux and macOS.</summary>
    public const int NoSuchEntry = 2;

    private const string LibC = "libc";
    private const int CurrentDirectory = -100;      // AT_FDCWD
    private const int DoNotFollowLinks = 0x100;     // AT_SYMLINK_NOFOLLOW
    private const uint BasicStats = 0x7FF;          // STATX_BASIC_STATS
    private const int NotEnoughRoom = 34;           // ERANGE
    private const int AccountBuffer = 16 * 1024;
    private const int MostAccountBuffer = 1024 * 1024;
    private const int StatBytes = 256;

    /// <summary>Reads what <c>stat</c> (or, not following links, <c>lstat</c>) says about <paramref name="path"/>.</summary>
    /// <param name="path">The path.</param>
    /// <param name="followLinks">Whether a link answers for its target.</param>
    /// <param name="stat">What it says.</param>
    /// <param name="error">The <c>errno</c> when it says nothing.</param>
    /// <returns>Whether it answered.</returns>
    /// <exception cref="ZabbixItemException">This C library does not have the function.</exception>
    public static bool TryStat(string path, bool followLinks, out ZabbixFileStat stat, out int error)
    {
        Span<byte> buffer = stackalloc byte[StatBytes];
        buffer.Clear();
        stat = default;
        try
        {
            int result = OperatingSystem.IsLinux() ? Statx(CurrentDirectory, path, followLinks ? 0 : DoNotFollowLinks, BasicStats, ref buffer[0])
                : !OperatingSystem.IsMacOS() ? throw ZabbixItemException.NotOnThisSystem()
                : RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? followLinks ? MacStat(path, ref buffer[0]) : MacLinkStat(path, ref buffer[0])
                    : followLinks ? MacStatX64(path, ref buffer[0]) : MacLinkStatX64(path, ref buffer[0]);
            error = result == 0 ? 0 : Marshal.GetLastPInvokeError();
            if (result != 0) return false;
        }
        catch (EntryPointNotFoundException missing)
        {
            // statx: glibc 2.28, musl 1.2.5.
            throw new ZabbixItemException("The file's details cannot be read: this system's C library has no statx.", missing);
        }
        stat = OperatingSystem.IsLinux() ? LinuxStat(buffer) : MacOsStat(buffer);
        return true;
    }

    /// <summary>Reads what <c>statvfs</c> says about the inodes of the file system <paramref name="path"/> is on (<c>get_fs_inode_stat</c>).</summary>
    /// <param name="path">A path on it.</param>
    /// <param name="inodes">What it says.</param>
    /// <param name="error">The <c>errno</c> when it says nothing.</param>
    /// <returns>Whether it answered.</returns>
    /// <exception cref="ZabbixItemException">The process is 32-bit Linux.</exception>
    public static bool TryInodes(string path, out ZabbixInodes inodes, out int error)
    {
        if (OperatingSystem.IsLinux() && IntPtr.Size != sizeof(long)) throw ZabbixItemException.NotOnThisSystem();
        Span<byte> buffer = stackalloc byte[StatBytes];
        buffer.Clear();
        inodes = default;
        if (StatVfs(path, ref buffer[0]) != 0)
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }
        error = 0;
        // Linux on 64 bits: bsize, frsize, blocks, bfree, bavail, files, ffree, favail, all 8 bytes. macOS: two 8-byte
        // sizes, then the six counters in 4 bytes each.
        (ulong files, ulong free, ulong available) = OperatingSystem.IsMacOS()
            ? (Read<uint>(buffer, 28), Read<uint>(buffer, 32), Read<uint>(buffer, 36))
            : (Read<ulong>(buffer, 40), Read<ulong>(buffer, 48), Read<ulong>(buffer, 56));
        // Blocks reserved for root are left out of the base the percentages are of.
        ulong total = files - (free - available);
        inodes = total == 0
            ? new ZabbixInodes(files, available, files - free, 100.0, 0.0)
            : new ZabbixInodes(files, available, files - free, 100.0 * available / total, 100.0 - (100.0 * available / total));
        return true;
    }

    /// <summary>The name of the account with this id, or <see langword="null"/> when it has none.</summary>
    public static string? UserName(uint id) => Account(id, static (uint number, nint entry, nint buffer, nuint length, out nint result) => UserById(number, entry, buffer, length, out result));

    /// <summary>The name of the group with this id, or <see langword="null"/> when it has none.</summary>
    public static string? GroupName(uint id) => Account(id, static (uint number, nint entry, nint buffer, nuint length, out nint result) => GroupById(number, entry, buffer, length, out result));

    private delegate int AccountLookup(uint id, nint entry, nint buffer, nuint length, out nint result);

    // getpwuid_r and getgrgid_r leave the name's pointer first in the entry they fill, pointing into the buffer.
    private static string? Account(uint id, AccountLookup lookup)
    {
        nint entry = Marshal.AllocHGlobal(256);
        try
        {
            for (int size = AccountBuffer; size <= MostAccountBuffer; size *= 2)
            {
                nint buffer = Marshal.AllocHGlobal(size);
                try
                {
                    int code = lookup(id, entry, buffer, (nuint)size, out nint found);
                    if (code == NotEnoughRoom) continue;
                    return code != 0 || found == 0 ? null : Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(entry));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(entry);
        }
    }

    // struct statx (linux/stat.h): the same on every architecture.
    private static ZabbixFileStat LinuxStat(ReadOnlySpan<byte> buffer) => new(
        TypeOf(Read<ushort>(buffer, 28)),
        Read<ushort>(buffer, 28) & 0xFFFu,
        Read<uint>(buffer, 20),
        Read<uint>(buffer, 24),
        Read<uint>(buffer, 16),
        ((ulong)Read<uint>(buffer, 136) << 32) | Read<uint>(buffer, 140),
        Read<ulong>(buffer, 32),
        Read<ulong>(buffer, 40),
        Read<ulong>(buffer, 48),
        Read<long>(buffer, 64),
        Read<long>(buffer, 112),
        Read<long>(buffer, 96));

    // struct stat with 64-bit inodes (sys/stat.h).
    private static ZabbixFileStat MacOsStat(ReadOnlySpan<byte> buffer) => new(
        TypeOf(Read<ushort>(buffer, 4)),
        Read<ushort>(buffer, 4) & 0xFFFu,
        Read<uint>(buffer, 16),
        Read<uint>(buffer, 20),
        Read<ushort>(buffer, 6),
        Read<uint>(buffer, 0),
        Read<ulong>(buffer, 8),
        (ulong)Read<long>(buffer, 96),
        (ulong)Read<long>(buffer, 104),
        Read<long>(buffer, 32),
        Read<long>(buffer, 48),
        Read<long>(buffer, 64));

    private static ZabbixFileType TypeOf(ushort mode) => (mode & 0xF000) switch
    {
        0x8000 => ZabbixFileType.File,
        0x4000 => ZabbixFileType.Directory,
        0xA000 => ZabbixFileType.Symlink,
        0xC000 => ZabbixFileType.Socket,
        0x6000 => ZabbixFileType.Block,
        0x2000 => ZabbixFileType.Character,
        0x1000 => ZabbixFileType.Fifo,
        _ => ZabbixFileType.None,
    };

    private static T Read<T>(ReadOnlySpan<byte> buffer, int offset)
        where T : unmanaged
        => MemoryMarshal.Read<T>(buffer[offset..]);

    [LibraryImport(LibC, EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(int directory, string path, int flags, uint mask, ref byte buffer);

    [LibraryImport(LibC, EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MacStat(string path, ref byte buffer);

    [LibraryImport(LibC, EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MacLinkStat(string path, ref byte buffer);

    [LibraryImport(LibC, EntryPoint = "stat$INODE64", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MacStatX64(string path, ref byte buffer);

    [LibraryImport(LibC, EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MacLinkStatX64(string path, ref byte buffer);

    [LibraryImport(LibC, EntryPoint = "statvfs", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int StatVfs(string path, ref byte buffer);

    [LibraryImport(LibC, EntryPoint = "getpwuid_r")]
    private static partial int UserById(uint id, nint entry, nint buffer, nuint length, out nint result);

    [LibraryImport(LibC, EntryPoint = "getgrgid_r")]
    private static partial int GroupById(uint id, nint entry, nint buffer, nuint length, out nint result);
}
