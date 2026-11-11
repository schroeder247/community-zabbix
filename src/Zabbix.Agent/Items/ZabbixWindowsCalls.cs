// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

// A DLL the platform calls name comes from the system's own directory, never from beside the application.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Community.Zabbix.Items;

/// <summary>The Windows functions the bundled keys call (docs/adr/0009-bundled-items.md §6).</summary>
[SupportedOSPlatform("windows")]
internal static partial class ZabbixWindowsCalls
{
    private const string Kernel32 = "kernel32.dll";
    private const string User32 = "user32.dll";
    private const uint QueryLimitedInformation = 0x1000;        // PROCESS_QUERY_LIMITED_INFORMATION
    private const uint ReadAttributes = 0x80;                   // FILE_READ_ATTRIBUTES
    private const uint ShareEverything = 7;                     // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    private const uint OpenExisting = 3;                        // OPEN_EXISTING
    private const uint BackupSemantics = 0x02000000;            // FILE_FLAG_BACKUP_SEMANTICS: what opens a directory
    private const uint OpenReparsePoint = 0x00200000;           // FILE_FLAG_OPEN_REPARSE_POINT
    private const int FileBasicInfo = 0;                        // FILE_INFO_BY_HANDLE_CLASS
    private const uint InvalidFileSize = 0xFFFFFFFF;            // INVALID_FILE_SIZE
    private const long FileEpochToUnix = 11644473600;           // seconds from 1601, FILETIME's epoch, to 1970

    /// <summary><c>MEMORYSTATUSEX</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    /// <summary><c>IO_COUNTERS</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperations;
        public ulong WriteOperations;
        public ulong OtherOperations;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
    }

    /// <summary><c>PROCESS_MEMORY_COUNTERS</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryCounters
    {
        public uint Size;
        public uint PageFaults;
        public nuint PeakWorkingSet;
        public nuint WorkingSet;
        public nuint QuotaPeakPagedPool;
        public nuint QuotaPagedPool;
        public nuint QuotaPeakNonPagedPool;
        public nuint QuotaNonPagedPool;
        public nuint PageFile;
        public nuint PeakPageFile;
    }

    /// <summary><c>PERFORMANCE_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint Handles;
        public uint Processes;
        public uint Threads;
    }

    /// <summary><c>FILE_BASIC_INFO</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasic
    {
        public long Creation;
        public long LastAccess;
        public long LastWrite;
        public long Change;
        public uint Attributes;
        public uint Padding;
    }

    /// <summary><c>BY_HANDLE_FILE_INFORMATION</c>, its three times as the pairs of words they are.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileByHandle
    {
        public uint Attributes;
        public uint CreationLow;
        public uint CreationHigh;
        public uint AccessLow;
        public uint AccessHigh;
        public uint WriteLow;
        public uint WriteHigh;
        public uint VolumeSerial;
        public uint SizeHigh;
        public uint SizeLow;
        public uint Links;
        public uint IndexHigh;
        public uint IndexLow;
    }

    /// <summary>A file's identity, for telling hard links apart, and how many it has.</summary>
    public readonly record struct FileIdentity(uint Links, uint Volume, ulong Index);

    /// <summary><c>GlobalMemoryStatusEx</c>.</summary>
    /// <exception cref="ZabbixItemException">Windows did not answer.</exception>
    public static MemoryStatus Memory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? status : throw new ZabbixItemException("The system's figures cannot be read.");
    }

    /// <summary><c>GetPerformanceInfo</c>'s <c>SystemCache</c>, in bytes.</summary>
    /// <exception cref="ZabbixItemException">Windows did not answer.</exception>
    public static ulong CachedBytes()
    {
        if (!GetPerformanceInfo(out PerformanceInformation info, (uint)Marshal.SizeOf<PerformanceInformation>())) throw new ZabbixItemException("The system's figures cannot be read.");
        return (ulong)info.SystemCache * info.PageSize;
    }

    /// <summary><c>GetSystemTimes</c>: 100-nanosecond ticks; the kernel's include the idle ones.</summary>
    /// <exception cref="ZabbixItemException">Windows did not answer.</exception>
    public static (ulong Idle, ulong Kernel, ulong User) SystemTimes()
        => GetSystemTimes(out ulong idle, out ulong kernel, out ulong user) ? (idle, kernel, user) : throw new ZabbixItemException("The processors' figures cannot be read.");

    /// <summary>A handle to read a process's counters with, or <see langword="null"/> when it may not be opened or has gone.</summary>
    public static SafeProcessHandle? Query(int process)
    {
        SafeProcessHandle handle = OpenProcess(QueryLimitedInformation, inheritHandle: false, (uint)process);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    /// <summary><c>GetProcessMemoryInfo</c>.</summary>
    public static bool TryMemory(SafeProcessHandle process, out MemoryCounters counters)
        => GetProcessMemoryInfo(process, out counters, (uint)Marshal.SizeOf<MemoryCounters>());

    /// <summary><c>GetProcessIoCounters</c>.</summary>
    public static bool TryIo(SafeProcessHandle process, out IoCounters counters) => GetProcessIoCounters(process, out counters);

    /// <summary><c>GetGuiResources</c>: 0 is the GDI objects, 1 the USER objects.</summary>
    public static uint GuiObjects(SafeProcessHandle process, uint which) => GetGuiResources(process, which);

    /// <summary>When a path's contents or attributes last changed (<c>FILE_BASIC_INFO.ChangeTime</c>), in seconds since the epoch.</summary>
    /// <exception cref="ZabbixItemException">Windows did not answer.</exception>
    public static long ChangeTime(string path, bool followLinks)
    {
        using SafeFileHandle file = Open(path, followLinks);
        if (!GetFileInformationByHandleEx(file, FileBasicInfo, out FileBasic basic, (uint)Marshal.SizeOf<FileBasic>()))
            throw new ZabbixItemException($"The file's details cannot be read: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        return (basic.Change / TimeSpan.TicksPerSecond) - FileEpochToUnix;
    }

    /// <summary>The file a path names, or <see langword="null"/> when it cannot be opened.</summary>
    public static FileIdentity? Identity(string path)
    {
        using SafeFileHandle file = CreateFile(path, ReadAttributes, ShareEverything, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);
        if (file.IsInvalid || !GetFileInformationByHandle(file, out FileByHandle info)) return null;
        return new FileIdentity(info.Links, info.VolumeSerial, ((ulong)info.IndexHigh << 32) | info.IndexLow);
    }

    /// <summary>The bytes a file takes (<c>GetCompressedFileSize</c>), or <see langword="null"/> when it does not say.</summary>
    public static ulong? CompressedSize(string path)
    {
        uint low = GetCompressedFileSize(path, out uint high);
        return low == InvalidFileSize && Marshal.GetLastPInvokeError() != 0 ? null : ((ulong)high << 32) | low;
    }

    /// <summary>The size of the volume's clusters a path is on (<c>zbx_get_cluster_size</c>).</summary>
    /// <exception cref="ZabbixItemException">Windows did not answer.</exception>
    public static ulong ClusterSize(string path)
    {
        Span<char> volume = stackalloc char[1024];
        if (!GetVolumePathName(path, ref volume[0], (uint)volume.Length)) throw new ZabbixItemException($"The file system's details cannot be read: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        string root = volume[..volume.IndexOf('\0')].ToString();
        if (!GetDiskFreeSpace(root, out uint sectors, out uint bytes, out _, out _)) throw new ZabbixItemException($"The file system's details cannot be read: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        return (ulong)sectors * bytes;
    }

    private static SafeFileHandle Open(string path, bool followLinks)
    {
        SafeFileHandle file = CreateFile(path, ReadAttributes, ShareEverything, 0, OpenExisting, followLinks ? BackupSemantics : BackupSemantics | OpenReparsePoint, 0);
        if (!file.IsInvalid) return file;
        int error = Marshal.GetLastPInvokeError();
        file.Dispose();
        throw new ZabbixItemException($"The file's details cannot be read: {Marshal.GetPInvokeErrorMessage(error)}");
    }

    [LibraryImport(Kernel32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [LibraryImport(Kernel32, EntryPoint = "K32GetPerformanceInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPerformanceInfo(out PerformanceInformation information, uint size);

    [LibraryImport(Kernel32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [LibraryImport(Kernel32, SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint process);

    [LibraryImport(Kernel32, EntryPoint = "K32GetProcessMemoryInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(SafeProcessHandle process, out MemoryCounters counters, uint size);

    [LibraryImport(Kernel32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);

    [LibraryImport(User32)]
    private static partial uint GetGuiResources(SafeProcessHandle process, uint which);

    [LibraryImport(Kernel32, EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out FileBasic information, uint size);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out FileByHandle information);

    [LibraryImport(Kernel32, EntryPoint = "GetCompressedFileSizeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetCompressedFileSize(string path, out uint high);

    [LibraryImport(Kernel32, EntryPoint = "GetVolumePathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumePathName(string path, ref char volume, uint length);

    [LibraryImport(Kernel32, EntryPoint = "GetDiskFreeSpaceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);
}
