// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Community.Zabbix.Items;

/// <summary>The system calls the system keys make on macOS (docs/adr/0009-bundled-items.md §6).</summary>
[SupportedOSPlatform("macos")]
internal static partial class ZabbixMacCalls
{
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const int HostVmInfo64 = 4;             // HOST_VM_INFO64
    private const int VmInfoWords = 38;             // HOST_VM_INFO64_COUNT
    private const int ProcessorCpuLoadInfo = 2;     // PROCESSOR_CPU_LOAD_INFO
    private const int CpuStates = 4;                // CPU_STATE_MAX: user, system, idle, nice

    // mach_host_self adds a reference to the port each time, so it is asked for once. Lazily: loading this type must not
    // call anything, or naming it on another system would throw.
    private static readonly Lazy<int> s_host = new(HostSelf);

    // mach_task_self() is a macro over this variable of the C library's.
    private static readonly Lazy<int> s_task = new(static () => Marshal.ReadInt32(NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "mach_task_self_")));

    /// <summary>One processor's ticks in each state.</summary>
    public readonly record struct ProcessorTicks(ulong User, ulong System, ulong Idle, ulong Nice);

    /// <summary>The pages <c>host_statistics64</c> counts in each state.</summary>
    public readonly record struct Pages(ulong Free, ulong Active, ulong Inactive, ulong Wired);

    /// <summary>The swap <c>vm.swapusage</c> reports, in bytes.</summary>
    public readonly record struct SwapUsage(ulong Total, ulong Available, ulong Used);

    /// <summary>A <c>sysctl</c> value of 4 bytes.</summary>
    /// <exception cref="ZabbixItemException">The kernel did not answer.</exception>
    public static int Int32(string name)
    {
        Span<byte> value = stackalloc byte[sizeof(int)];
        Read(name, value);
        return MemoryMarshal.Read<int>(value);
    }

    /// <summary>A <c>sysctl</c> value of 8 bytes.</summary>
    /// <exception cref="ZabbixItemException">The kernel did not answer.</exception>
    public static ulong UInt64(string name)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        Read(name, value);
        return MemoryMarshal.Read<ulong>(value);
    }

    /// <summary><c>kern.boottime</c>'s seconds.</summary>
    /// <exception cref="ZabbixItemException">The kernel did not answer.</exception>
    public static long BootTime()
    {
        Span<byte> value = stackalloc byte[16];     // struct timeval: seconds, then microseconds
        Read("kern.boottime", value);
        return MemoryMarshal.Read<long>(value);
    }

    /// <summary><c>vm.swapusage</c>.</summary>
    /// <exception cref="ZabbixItemException">The kernel did not answer.</exception>
    public static SwapUsage Swap()
    {
        Span<byte> value = stackalloc byte[32];     // struct xsw_usage: total, available, used, then its page size
        Read("vm.swapusage", value);
        return new SwapUsage(MemoryMarshal.Read<ulong>(value), MemoryMarshal.Read<ulong>(value[8..]), MemoryMarshal.Read<ulong>(value[16..]));
    }

    /// <summary>The load averages <c>getloadavg</c> reports.</summary>
    /// <exception cref="ZabbixItemException">The kernel did not answer.</exception>
    public static double LoadAverage(int which)
    {
        Span<double> values = stackalloc double[3];
        if (GetLoadAverage(ref values[0], 3) <= which) throw new ZabbixItemException($"The load average cannot be read: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        return values[which];
    }

    /// <summary>The pages in each state.</summary>
    /// <exception cref="ZabbixItemException">The kernel did not answer.</exception>
    public static Pages MemoryPages()
    {
        Span<uint> info = stackalloc uint[VmInfoWords];     // struct vm_statistics64 starts with these four counts
        uint count = VmInfoWords;
        if (HostStatistics(s_host.Value, HostVmInfo64, ref info[0], ref count) != 0) throw new ZabbixItemException("The host's memory figures cannot be read.");
        return new Pages(info[0], info[1], info[2], info[3]);
    }

    /// <summary>Each processor's ticks since the host started.</summary>
    /// <exception cref="ZabbixItemException">The kernel did not answer.</exception>
    public static ProcessorTicks[] Processors()
    {
        if (HostProcessorInfo(s_host.Value, ProcessorCpuLoadInfo, out uint processors, out nint info, out uint words) != 0) throw new ZabbixItemException("The processors' figures cannot be read.");
        try
        {
            var ticks = new ProcessorTicks[processors];
            for (int i = 0; i < ticks.Length; i++)
            {
                ulong Tick(int state) => (uint)Marshal.ReadInt32(info, ((i * CpuStates) + state) * sizeof(uint));
                ticks[i] = new ProcessorTicks(Tick(0), Tick(1), Tick(2), Tick(3));
            }
            return ticks;
        }
        finally
        {
            _ = VmDeallocate(s_task.Value, (nuint)info, (nuint)words * sizeof(uint));
        }
    }

    private static void Read(string name, Span<byte> value)
    {
        nuint length = (nuint)value.Length;
        if (SysctlByName(name, ref value[0], ref length, 0, 0) != 0)
            throw new ZabbixItemException($"The system's figures cannot be read: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
    }

    [LibraryImport(LibSystem, EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int SysctlByName(string name, ref byte value, ref nuint length, nint newValue, nuint newLength);

    [LibraryImport(LibSystem, EntryPoint = "getloadavg", SetLastError = true)]
    private static partial int GetLoadAverage(ref double values, int count);

    [LibraryImport(LibSystem, EntryPoint = "mach_host_self")]
    private static partial int HostSelf();

    [LibraryImport(LibSystem, EntryPoint = "host_statistics64")]
    private static partial int HostStatistics(int host, int flavor, ref uint info, ref uint count);

    [LibraryImport(LibSystem, EntryPoint = "host_processor_info")]
    private static partial int HostProcessorInfo(int host, int flavor, out uint processors, out nint info, out uint words);

    [LibraryImport(LibSystem, EntryPoint = "vm_deallocate")]
    private static partial int VmDeallocate(int task, nuint address, nuint size);
}
