// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Community.Zabbix.Items;

/// <summary>
/// Provides Zabbix's system keys: the host's name and time, uptime and boot time, its processors and their load, its
/// operating system, memory, swap and kernel limits (<c>system.*</c>, <c>kernel.*</c>, <c>vm.memory.size</c>,
/// <c>vm.vmemory.size</c>). Each
/// answers as Zabbix's agent does: the same parameters, modes and units.
/// </summary>
/// <remarks>
/// On Linux the keys read the kernel's own files (<c>/proc</c>, <c>/sys</c>), as Zabbix's agent does. On Windows and
/// macOS a key asks the operating system's own functions where the .NET libraries do not have the figure
/// (docs/adr/0009-bundled-items.md §6), and is not supported where the system cannot answer as Zabbix's agent does; each
/// key's platforms are in the bundled items reference (<c>docs/guides/bundled-items.md</c>). <c>system.cpu.util</c> samples once
/// a second from its first request, which waits for two samples within the check's deadline; a provider built on its own
/// stops sampling when it is disposed.
/// </remarks>
public sealed partial class ZabbixSystemItems : ZabbixItemProvider, IDisposable
{
    // The release's major, minor and patch numbers, as system_sw_os_get's sscanf("%[0-9].%[0-9].%[0-9]") reads them.
    [GeneratedRegex(@"^([0-9]+)(?:\.([0-9]+)(?:\.([0-9]+))?)?", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex KernelRelease();

    private const string CpuDirectory = "/sys/devices/system/cpu";

    private readonly ZabbixItemSet _set = new();
    private readonly ZabbixItemCollector _collector;
    private readonly ZabbixItemCollector? _owned;
    private readonly ZabbixHostFiles _files;
    private readonly TimeProvider _time;

    /// <summary>The system keys of the host this process runs on.</summary>
    /// <param name="time">The clock <c>system.localtime</c> and <c>system.boottime</c> read and <c>system.cpu.util</c> samples on; the system clock by default.</param>
    public ZabbixSystemItems(TimeProvider? time = null)
        : this(ZabbixHostFiles.Host, time ?? TimeProvider.System)
    {
    }

    internal ZabbixSystemItems(ZabbixHostFiles files, TimeProvider time)
        : this(new ZabbixItemCollector(files, time), owned: true)
    {
    }

    internal ZabbixSystemItems(ZabbixItemCollector collector, bool owned = false)
    {
        _collector = collector;
        _owned = owned ? collector : null;
        _files = collector.Files;
        _time = collector.Time;
        _set.Add("system.hostname", "The host's name", HostName, "system.hostname[host]")
            .Add("system.localtime", "The host's time: seconds since the epoch, or its local time and offset", LocalTime, "system.localtime[local]")
            .Add("system.uptime", "Seconds since the host started", _ => ZabbixItemSet.Integer(Uptime()))
            .Add("system.boottime", "When the host started, in seconds since the epoch", _ => ZabbixItemSet.Integer(BootTime()))
            .Add("system.cpu.num", "The number of processors", CpuNum, "system.cpu.num[online]")
            .Add("system.cpu.discovery", "The processors, for low-level discovery", _ => CpuDiscovery())
            .Add("system.cpu.load", "The processor load average", CpuLoad, "system.cpu.load[all,avg1]")
            .Add("system.cpu.util", "The share of time the processors spend in a state, as a percentage", CpuUtilAsync, "system.cpu.util[all,user,avg1]")
            .Add("system.cpu.switches", "Context switches since the host started", _ => ProcStat("ctxt"))
            .Add("system.cpu.intr", "Device interrupts since the host started", _ => ProcStat("intr"))
            .Add("system.sw.arch", "The processor architecture", _ => Architecture())
            .Add("system.sw.os", "The operating system's version", SoftwareOs, "system.sw.os[name]")
            .Add("system.sw.os.get", "The operating system's version, as JSON", SoftwareOsGet)
            .Add("system.swap.size", "Swap space, in bytes or as a percentage", SwapSize, "system.swap.size[all,free]")
            .Add("system.swap.in", "Swap-in statistics", key => Swap(key, read: true), "system.swap.in[all]")
            .Add("system.swap.out", "Swap-out statistics", key => Swap(key, read: false), "system.swap.out[all]")
            .Add("system.hw.macaddr", "The network interfaces' MAC addresses", MacAddresses)
            .Add("system.hw.cpu", "The processors' vendor, model and frequency", HardwareCpu)
            .Add("kernel.maxfiles", "The most files the kernel opens", _ => Kernel("/proc/sys/fs/file-max", "kern.maxfiles"))
            .Add("kernel.maxproc", "The most processes the kernel runs", _ => Kernel("/proc/sys/kernel/pid_max", "kern.maxproc"))
            .Add("kernel.openfiles", "The file descriptors open now", _ => Kernel("/proc/sys/fs/file-nr", macName: null))
            .Add("vm.memory.size", "Memory, in bytes or as a percentage", MemorySize, "vm.memory.size[total]")
            .Add("vm.vmemory.size", "Virtual memory on Windows, in bytes or as a percentage", VirtualMemorySize, "vm.vmemory.size[total]");
    }

    /// <inheritdoc/>
    public override IReadOnlyList<ZabbixItemDefinition> Items => _set.Items;

    /// <inheritdoc/>
    public override ValueTask<ZabbixAgentAnswer> GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken) => _set.AnswerAsync(key, cancellationToken);

    /// <summary>Stops <c>system.cpu.util</c>'s sampling.</summary>
    public void Dispose() => _owned?.Dispose();

    private static string HostName(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 2);
        string type = ZabbixItemSet.Parameter(key, 0);
        string name = Environment.MachineName;
        if (type.Length == 0) type = OperatingSystem.IsWindows() ? "netbios" : "host";
        name = type switch
        {
            "host" => OperatingSystem.IsWindows() ? Dns.GetHostName() : name,
            "shorthost" => (OperatingSystem.IsWindows() ? Dns.GetHostName() : name).Split('.')[0],
            "fqdn" => Dns.GetHostEntry(Dns.GetHostName()).HostName,
            "netbios" when OperatingSystem.IsWindows() => name,
            "netbios" => throw new ZabbixItemException("Only Windows has a NetBIOS name."),
            _ => throw ZabbixItemException.InvalidParameter(0),
        };
#pragma warning disable CA1308 // Zabbix's "lower" transform lowercases the name; nothing is normalized for comparison
        return ZabbixItemSet.Mode(key, 1, "none", "lower") == "lower" ? name.ToLowerInvariant() : name;
#pragma warning restore CA1308
    }

    private string LocalTime(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 1);
        if (ZabbixItemSet.Mode(key, 0, "utc", "local") == "utc") return ZabbixItemSet.Integer(_time.GetUtcNow().ToUnixTimeSeconds());
        DateTimeOffset now = _time.GetLocalNow();
        TimeSpan offset = now.Offset;
        return string.Create(CultureInfo.InvariantCulture,
            $"{now:yyyy-MM-dd,HH:mm:ss.fff},{(offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}");
    }

    private ulong Uptime()
    {
        if (_files.IsLinux)
        {
            string text = _files.ReadAllText("/proc/uptime");
            int end = text.IndexOf('.', StringComparison.Ordinal);
            return ulong.Parse(text.AsSpan(0, end < 0 ? text.IndexOf(' ', StringComparison.Ordinal) : end), NumberStyles.None, CultureInfo.InvariantCulture);
        }
#pragma warning disable RS0030 // ADR 0009: the host's own uptime (GetTickCount64 counts sleep too), not an elapsed time this process measures
        if (OperatingSystem.IsWindows()) return (ulong)(Environment.TickCount64 / 1000);
#pragma warning restore RS0030
        // system_uptime (osx/uptime.c): the tick count stops while a Mac sleeps, so the boot time answers.
        if (OperatingSystem.IsMacOS()) return (ulong)(_time.GetUtcNow().ToUnixTimeSeconds() - ZabbixMacCalls.BootTime());
        throw ZabbixItemException.NotOnThisSystem();
    }

    private ulong BootTime()
    {
        if (_files.IsLinux)
        {
            foreach (string line in _files.ReadLines("/proc/stat"))
            {
                if (line.StartsWith("btime ", StringComparison.Ordinal)) return ulong.Parse(line.AsSpan(6).Trim(), NumberStyles.None, CultureInfo.InvariantCulture);
            }
            throw new ZabbixItemException("/proc/stat has no \"btime\" line.");
        }
        if (OperatingSystem.IsWindows()) return (ulong)_time.GetUtcNow().ToUnixTimeSeconds() - Uptime();
        if (OperatingSystem.IsMacOS()) return (ulong)ZabbixMacCalls.BootTime();
        throw ZabbixItemException.NotOnThisSystem();
    }

    private string CpuNum(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 1);
        string type = ZabbixItemSet.Mode(key, 0, "online", "max");
        // get_cpu_num (osx/cpu.c): HW_AVAILCPU and HW_NCPU.
        if (!_files.IsLinux && OperatingSystem.IsMacOS()) return ZabbixItemSet.Integer(ZabbixMacCalls.Int32(type == "online" ? "hw.activecpu" : "hw.ncpu"));
        if (!_files.IsLinux)
            return type == "online" ? ZabbixItemSet.Integer(Environment.ProcessorCount) : throw ZabbixItemException.InvalidParameter(0);
        return ZabbixItemSet.Integer(type == "online" ? OnlineCpus().Count : ConfiguredCpus().Count);
    }

    // _SC_NPROCESSORS_ONLN and _SC_NPROCESSORS_CONF as glibc reads them: the online list, and the cpuN directories.
    private HashSet<int> OnlineCpus()
        => [.. ZabbixHostFiles.CpuList(_files.ReadFirstLine(CpuDirectory + "/online"))];

    private List<int> ConfiguredCpus()
        => [.. _files.Directories(CpuDirectory)
            .Where(name => name.Length > 3 && name.StartsWith("cpu", StringComparison.Ordinal) && name.AsSpan(3).ContainsAnyExceptInRange('0', '9') is false)
            .Select(name => int.Parse(name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture))
            .Order()];

    private string CpuDiscovery()
    {
        HashSet<int> online = _files.IsLinux ? OnlineCpus() : [];
        IEnumerable<(int Number, string Status)> cpus = _files.IsLinux
            ? ConfiguredCpus().Select(cpu => (cpu, online.Contains(cpu) ? "online" : "offline"))
            : Enumerable.Range(0, Environment.ProcessorCount).Select(cpu => (cpu, "online"));
        return ZabbixItemSet.Json(json =>
        {
            json.WriteStartArray();
            foreach ((int number, string status) in cpus)
            {
                json.WriteStartObject();
                json.WriteNumber("{#CPU.NUMBER}", number);
                json.WriteString("{#CPU.STATUS}", status);
                json.WriteEndObject();
            }
            json.WriteEndArray();
        });
    }

    private string CpuLoad(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 2);
        bool perCpu = ZabbixItemSet.Mode(key, 0, "all", "percpu") == "percpu";
        int mode = ZabbixItemSet.Mode(key, 1, "avg1", "avg5", "avg15") switch { "avg1" => 0, "avg5" => 1, _ => 2 };
        if (!_files.IsLinux && OperatingSystem.IsMacOS())
        {
            double average = ZabbixMacCalls.LoadAverage(mode);
            return ZabbixItemSet.Float(perCpu ? average / ZabbixMacCalls.Int32("hw.activecpu") : average);
        }
        if (!_files.IsLinux) throw ZabbixItemException.NotOnThisSystem();
        string[] fields = _files.ReadFirstLine("/proc/loadavg").Split(' ');
        if (fields.Length <= mode || !double.TryParse(fields[mode], NumberStyles.Float, CultureInfo.InvariantCulture, out double load))
            throw new ZabbixItemException("The load average cannot be read.");
        return ZabbixItemSet.Float(perCpu ? load / OnlineCpus().Count : load);
    }

    // system_cpu_util (linux/cpu.c): processor, state, mode.
    private async ValueTask<string> CpuUtilAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        ZabbixItemSet.AtMost(key, 3);
        string which = ZabbixItemSet.Parameter(key, 0);
        int processor = ZabbixProcessorSamples.All;
        if (which.Length != 0 && which != "all" && (!int.TryParse(which, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out processor) || processor < ZabbixProcessorSamples.All))
            throw ZabbixItemException.InvalidParameter(0);
        // Windows has one type (system_cpu_util, win32/cpu.c): the processors' busy time. A Mac counts four states.
        bool windows = !_files.IsLinux && OperatingSystem.IsWindows();
        bool mac = !_files.IsLinux && OperatingSystem.IsMacOS();
        string type = ZabbixItemSet.Parameter(key, 1);
        int state = windows
            ? type is "" or "system" ? 2 : throw ZabbixItemException.InvalidParameter(1)
            : type switch
            {
                "" or "user" => 0,
                "nice" => 1,
                "system" => 2,
                "idle" => 3,
                "iowait" when !mac => 4,
                "interrupt" when !mac => 5,
                "softirq" when !mac => 6,
                "steal" when !mac => 7,
                "guest" when !mac => 8,
                "guest_nice" when !mac => 9,
                _ => throw ZabbixItemException.InvalidParameter(1),
            };
        int seconds = ZabbixItemSet.Window(key, 2);
        if (!_files.IsLinux && !windows && !mac) throw ZabbixItemException.NotOnThisSystem();
        if (windows && processor != ZabbixProcessorSamples.All) throw new ZabbixItemException("Windows reports the processors' time only for all of them together.");
        return ZabbixItemSet.Float(await _collector.Processors.UtilizationAsync(processor, state, seconds, cancellationToken).ConfigureAwait(false));
    }

    private string ProcStat(string name)
    {
        if (!_files.IsLinux) throw ZabbixItemException.NotOnThisSystem();
        foreach (string line in _files.ReadLines("/proc/stat"))
        {
            if (!line.StartsWith(name, StringComparison.Ordinal)) continue;
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 1 && ulong.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong value)) return ZabbixItemSet.Integer(value);
        }
        throw new ZabbixItemException($"/proc/stat has no \"{name}\" line.");
    }

    // uname()'s machine on Unix; Windows' own names (system_sw_arch, win32/software.c).
    private static string Architecture()
    {
        System.Runtime.InteropServices.Architecture architecture = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsWindows())
        {
            return architecture switch
            {
                System.Runtime.InteropServices.Architecture.X86 => "x86",
                System.Runtime.InteropServices.Architecture.X64 => "x64",
                _ => throw new ZabbixItemException("This processor architecture has no name to report."),
            };
        }
        return Machine(architecture);
    }

    private static string Machine(System.Runtime.InteropServices.Architecture architecture) => architecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x86_64",
        System.Runtime.InteropServices.Architecture.X86 => "i686",
        System.Runtime.InteropServices.Architecture.Arm64 => OperatingSystem.IsMacOS() ? "arm64" : "aarch64",
        System.Runtime.InteropServices.Architecture.Arm => "armv7l",
        System.Runtime.InteropServices.Architecture.S390x => "s390x",
        System.Runtime.InteropServices.Architecture.Ppc64le => "ppc64le",
        System.Runtime.InteropServices.Architecture.RiscV64 => "riscv64",
        System.Runtime.InteropServices.Architecture.LoongArch64 => "loongarch64",
        _ => architecture.ToString(),
    };

    private string SoftwareOs(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 1);
        string info = ZabbixItemSet.Mode(key, 0, "full", "short", "name");
        if (!_files.IsLinux && OperatingSystem.IsWindows()) return WindowsOs(info);
        if (!_files.IsLinux) throw ZabbixItemException.NotOnThisSystem();
        return info switch
        {
            "full" => _files.ReadFirstLine("/proc/version"),
            "short" => _files.ReadFirstLine("/proc/version_signature"),
            _ => OsName() ?? throw new ZabbixItemException("Cannot open /etc/issue.net."),
        };
    }

    // PRETTY_NAME from /etc/os-release, else the first line of /etc/issue.net (get_os_name).
    private string? OsName()
    {
        foreach (string line in _files.TryReadAllText("/etc/os-release")?.Split('\n') ?? [])
        {
            if (!line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)) continue;
            string value = line["PRETTY_NAME=".Length..];
            if (value.StartsWith('"')) return value[1..].Split('"')[0].TrimEnd();
            if (value.Length > 0) return value.Split(' ', '\t')[0];
        }
        string? issue = _files.TryReadAllText("/etc/issue.net");
        return issue?.Split('\n')[0].TrimEnd();
    }

    private string SoftwareOsGet(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 0);
        if (!_files.IsLinux && OperatingSystem.IsWindows()) return WindowsOsGet();
        if (!_files.IsLinux) throw ZabbixItemException.NotOnThisSystem();
        string? name = OsName();
        string machine = Machine(RuntimeInformation.OSArchitecture);
        string? release = _files.TryReadAllText("/proc/sys/kernel/osrelease")?.Trim();
        string? full = _files.TryReadAllText("/proc/version")?.Split('\n')[0].TrimEnd();
        return ZabbixItemSet.Json(json =>
        {
            json.WriteStartObject();
            json.WriteString("os_type", "linux");
            if (name is not null) json.WriteString("product_name", name);
            json.WriteString("architecture", machine);
            if (release is { Length: > 0 })
            {
                Match version = KernelRelease().Match(release);
                if (version.Success) json.WriteString("kernel_major", version.Groups[1].Value);
                if (version.Groups[2].Success) json.WriteString("kernel_minor", version.Groups[2].Value);
                if (version.Groups[3].Success) json.WriteString("kernel_patch", version.Groups[3].Value);
                json.WriteString("kernel", release);
            }
            string pretty = string.Join(' ', new[] { name, machine, release }.Where(part => !string.IsNullOrEmpty(part)));
            if (pretty.Length > 0) json.WriteString("version_pretty", pretty);
            json.WriteString("version_full", full ?? string.Empty);
            json.WriteEndObject();
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string WindowsOs(string info)
    {
        using RegistryKey version = WindowsVersionKey();
        string? text = info switch
        {
            "full" => WindowsFull(version),
            "short" => WindowsPretty(version),
            _ => RegistryString(version, "ProductName"),
        };
        return string.IsNullOrEmpty(text) ? throw new ZabbixItemException("Could not read registry value ProductName.") : text;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string WindowsOsGet()
    {
        using RegistryKey version = WindowsVersionKey();
        string architecture = RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => "unknown",
        };
        return ZabbixItemSet.Json(json =>
        {
            json.WriteStartObject();
            json.WriteString("os_type", "windows");
            WriteIf("product_name", RegistryString(version, "ProductName"));
            json.WriteString("architecture", architecture);
            WriteIf("build_number", RegistryString(version, "CurrentBuildNumber"));
            WriteIf("build_revision", RegistryString(version, "UBR"));
            WriteIf("build", WindowsBuild(version, descriptor: false));
            WriteIf("edition", RegistryString(version, "EditionID"));
            WriteIf("composition", RegistryString(version, "CompositionEditionID"));
            WriteIf("display_version", RegistryString(version, "DisplayVersion"));
            WriteIf("sp_version", RegistryString(version, "CSDVersion"));
            WriteIf("sp_build", RegistryString(version, "CSDBuildNumber"));
            WriteIf("version", RegistryString(version, "CurrentVersion"));
            WriteIf("version_pretty", WindowsPretty(version));
            json.WriteString("version_full", WindowsFull(version));
            json.WriteEndObject();

            void WriteIf(string name, string? value)
            {
                if (value is not null) json.WriteString(name, value);
            }
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static RegistryKey WindowsVersionKey()
        => Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")
            ?? throw new ZabbixItemException(@"Could not open registry key HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion.");

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? RegistryString(RegistryKey key, string name) => key.GetValue(name) switch
    {
        null => null,
        int number => ((uint)number).ToString(CultureInfo.InvariantCulture),
        string[] lines => string.Join('\n', lines),
        object value => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string WindowsBuild(RegistryKey key, bool descriptor)
    {
        string? major = RegistryString(key, "CurrentBuildNumber");
        string build = string.Join('.', new[] { major, major is null ? null : RegistryString(key, "UBR") }.Where(part => !string.IsNullOrEmpty(part)));
        return descriptor && build.Length > 0 ? "Build " + build : build;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string WindowsFull(RegistryKey key)
        => string.Join(' ', new[] { RegistryString(key, "ProductName"), RegistryString(key, "BuildLabEx") ?? RegistryString(key, "BuildLab"), WindowsBuild(key, descriptor: true) }
            .Where(part => !string.IsNullOrEmpty(part)));

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string WindowsPretty(RegistryKey key)
        => string.Join(' ', new[] { RegistryString(key, "ProductName"), WindowsBuild(key, descriptor: true), RegistryString(key, "CSDVersion") }
            .Where(part => !string.IsNullOrEmpty(part)));

    private string SwapSize(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 2);
        if (ZabbixItemSet.Parameter(key, 0) is { Length: > 0 } device && device != "all") throw ZabbixItemException.InvalidParameter(0);
        if (!_files.IsLinux && OperatingSystem.IsWindows()) return WindowsSwapSize(key);
        string type = ZabbixItemSet.Mode(key, 1, "free", "total", "used", "pfree", "pused");
        if (!_files.IsLinux && OperatingSystem.IsMacOS())
        {
            ZabbixMacCalls.SwapUsage swap = ZabbixMacCalls.Swap();
            return SwapAnswer(type, swap.Total, swap.Available);
        }
        if (!_files.IsLinux) throw ZabbixItemException.NotOnThisSystem();
        string[] meminfo = _files.ReadLines("/proc/meminfo");
        ulong total = ZabbixHostFiles.KiloBytes(meminfo, "SwapTotal:") ?? throw new ZabbixItemException("The system's figures cannot be read.");
        ulong free = ZabbixHostFiles.KiloBytes(meminfo, "SwapFree:") ?? throw new ZabbixItemException("The system's figures cannot be read.");
        return SwapAnswer(type, total, free);
    }

    private static string SwapAnswer(string type, ulong total, ulong free) => type switch
    {
        "free" => ZabbixItemSet.Integer(free),
        "total" => ZabbixItemSet.Integer(total),
        "used" => ZabbixItemSet.Integer(total - free),
        "pfree" => ZabbixItemSet.Float(total != 0 ? 100.0 * (free / (double)total) : 100.0),
        _ => ZabbixItemSet.Float(total != 0 ? 100.0 - (100.0 * (free / (double)total)) : 0.0),
    };

    // system_swap_size (win32/swap.c): Windows reports commit, physical memory and page files together, so swap is what
    // is left without physical memory, bounded so that a virtual machine's odd figures cannot go negative.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string WindowsSwapSize(ZabbixItemKey key)
    {
        string type = ZabbixItemSet.Mode(key, 1, "total", "free", "pfree", "used");
        ZabbixWindowsCalls.MemoryStatus memory = ZabbixWindowsCalls.Memory();
        ulong total = memory.TotalPageFile > memory.TotalPhysical ? memory.TotalPageFile - memory.TotalPhysical : 0;
        ulong free = memory.AvailablePageFile > memory.AvailablePhysical ? memory.AvailablePageFile - memory.AvailablePhysical : 0;
        return SwapAnswer(type, total, Math.Min(free, total));
    }

    // system.swap.in/out (linux/swap.c): pages from /proc/vmstat for all swap; count and sectors from each swap
    // device's /proc/diskstats line, the device named by /proc/swaps and found by its major:minor in /sys/class/block.
    private string Swap(ZabbixItemKey key, bool read)
    {
        ZabbixItemSet.AtMost(key, 2);
        if (!_files.IsLinux) throw ZabbixItemException.NotOnThisSystem();
        string device = ZabbixItemSet.Parameter(key, 0);
        bool all = device.Length == 0 || device == "all";
        string mode = ZabbixItemSet.Parameter(key, 1);
        if (mode.Length == 0 || mode == "pages")
        {
            if (!all) throw ZabbixItemException.InvalidParameter(1);
            ulong? pages = null;
            foreach (string line in _files.TryReadAllText("/proc/vmstat")?.Split('\n') ?? [])
            {
                if (line.StartsWith(read ? "pswpin " : "pswpout ", StringComparison.Ordinal))
                    pages = ulong.Parse(line.AsSpan(line.IndexOf(' ', StringComparison.Ordinal) + 1), NumberStyles.None, CultureInfo.InvariantCulture);
            }
            return ZabbixItemSet.Integer(pages ?? throw new ZabbixItemException("The swap figures cannot be read."));
        }
        if (mode is not ("sectors" or "count")) throw ZabbixItemException.InvalidParameter(1);
        string wanted = all ? string.Empty : device.StartsWith("/dev/", StringComparison.Ordinal) ? device : "/dev/" + device;
        ulong ios = 0, sectors = 0;
        bool found = false;
        string[] diskstats = _files.TryReadAllText("/proc/diskstats")?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];
        foreach (string line in _files.TryReadAllText("/proc/swaps")?.Split('\n') ?? [])
        {
            if (!line.StartsWith("/dev/", StringComparison.Ordinal)) continue;
            string name = line.Split([' ', '\t'], 2)[0];
            if (!all && name != wanted) continue;
            string block = Path.GetFileName(ZabbixHostFiles.LinkTarget(_files.Resolve(name)) ?? name);
            string? numbers = _files.TryReadAllText($"/sys/class/block/{block}/dev")?.Trim();
            foreach (string stat in diskstats)
            {
                string[] fields = stat.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 10 || $"{fields[0]}:{fields[1]}" != numbers) continue;
                // rio rsect wio wsect: fields 3, 5, 7 and 9 (rio, rmerge, rsect, ..., wio, wmerge, wsect)
                ios += ulong.Parse(fields[read ? 3 : 7], NumberStyles.None, CultureInfo.InvariantCulture);
                sectors += ulong.Parse(fields[read ? 5 : 9], NumberStyles.None, CultureInfo.InvariantCulture);
                found = true;
                break;
            }
        }
        if (!found) throw new ZabbixItemException("The swap figures cannot be read.");
        return ZabbixItemSet.Integer(mode == "count" ? ios : sectors);
    }

    // system_hw_macaddr (linux/hardware.c): the non-loopback interfaces whose names match, "[name] aa:bb:…" sorted,
    // or the distinct addresses alone.
    private static string MacAddresses(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 2);
        bool names = ZabbixItemSet.Mode(key, 1, "full", "short") == "full";
        Regex? filter = ZabbixItemSet.Parameter(key, 0) == "all" ? null : ZabbixItemSet.Pattern(key, 0);
        var addresses = new List<string>();
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback || (filter is not null && !filter.IsMatch(adapter.Name))) continue;
            byte[] bytes = adapter.GetPhysicalAddress().GetAddressBytes();
            if (bytes.Length != 6) continue;
            string address = string.Join(':', bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            string entry = names ? $"[{adapter.Name}  {address}" : address;
            if (!names && addresses.Contains(entry)) continue;
            addresses.Add(entry);
        }
        addresses.Sort(StringComparer.Ordinal);
        return string.Join(", ", addresses.Select(entry => names ? ReplaceFirst(entry, ' ', ']') : entry));

        static string ReplaceFirst(string text, char from, char to)
        {
            int at = text.IndexOf(from, StringComparison.Ordinal);
            return at < 0 ? text : string.Concat(text.AsSpan(0, at), to.ToString(), text.AsSpan(at + 1));
        }
    }

    // system_hw_cpu (linux/hardware.c): /proc/cpuinfo's vendor, model and MHz per processor, with cpufreq's maximum.
    private string HardwareCpu(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 2);
        string which = ZabbixItemSet.Parameter(key, 0);
        int cpu = -1;
        if (which.Length != 0 && which != "all" && !int.TryParse(which, NumberStyles.None, CultureInfo.InvariantCulture, out cpu)) throw ZabbixItemException.InvalidParameter(0);
        string filter = ZabbixItemSet.Mode(key, 1, "full", "maxfreq", "vendor", "model", "curfreq");
        if (!_files.IsLinux) throw ZabbixItemException.NotOnThisSystem();
        var buffer = new StringBuilder();
        int current = -1;
        ulong? maxFrequency = null, currentFrequency = null;
        bool answered = false;
        foreach (string line in _files.ReadLines("/proc/cpuinfo"))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0 || colon + 1 >= line.Length) continue;
            string name = line[..colon];
            string value = line[(colon + 1)..].TrimStart().TrimEnd('\n');
            if (value.Length == 0) continue;
            if (name.StartsWith("processor", StringComparison.Ordinal))
            {
                if (current != -1 && (cpu == -1 || cpu == current)) PrintFrequency();
                currentFrequency = null;
                current = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : 0;
                if (cpu != -1 && cpu != current) continue;
                if (cpu == -1 || filter == "full") buffer.Append(CultureInfo.InvariantCulture, $"\nprocessor {current}:");
                if (filter is "full" or "maxfreq")
                {
                    maxFrequency = null;
                    if (_files.TryReadAllText($"/sys/devices/system/cpu/cpu{current}/cpufreq/cpuinfo_max_freq") is { } max
                        && ulong.TryParse(max.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong kHz))
                    {
                        maxFrequency = kHz;
                        answered = true;
                    }
                }
            }
            if (cpu != -1 && cpu != current) continue;
            if (name.StartsWith("vendor_id", StringComparison.Ordinal) && filter is "full" or "vendor")
            {
                answered = true;
                buffer.Append(' ').Append(value);
            }
            else if (name.StartsWith("model name", StringComparison.Ordinal) && filter is "full" or "model")
            {
                answered = true;
                buffer.Append(' ').Append(value);
            }
            else if (name.StartsWith("cpu MHz", StringComparison.Ordinal) && filter is "full" or "curfreq")
            {
                answered = true;
                int digits = 0;
                while (digits < value.Length && char.IsAsciiDigit(value[digits])) digits++;
                if (digits == 0) throw new ZabbixItemException("/proc/cpuinfo's frequency cannot be read.");
                currentFrequency = ulong.Parse(value.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture);
            }
        }
        if (!answered) throw new ZabbixItemException("The processors' figures cannot be read.");
        if (current != -1 && (cpu == -1 || cpu == current)) PrintFrequency();
        return buffer.Length > 0 ? buffer.ToString(1, buffer.Length - 1) : string.Empty;     // a leading space or newline

        void PrintFrequency()
        {
            if (filter == "maxfreq" && maxFrequency is { } max)
            {
                if (cpu == -1) buffer.Append(CultureInfo.InvariantCulture, $" {max / 1000}MHz");
                else buffer.Append(CultureInfo.InvariantCulture, $" {max * 1000}");
            }
            else if (filter == "curfreq" && currentFrequency is { } now)
            {
                if (cpu == -1) buffer.Append(CultureInfo.InvariantCulture, $" {now}MHz");
                else buffer.Append(CultureInfo.InvariantCulture, $" {now * 1_000_000}");
            }
            else if (filter == "full")
            {
                if (currentFrequency is { } working) buffer.Append(CultureInfo.InvariantCulture, $" working at {working}MHz");
                if (maxFrequency is { } maximum) buffer.Append(CultureInfo.InvariantCulture, $" (maximum {maximum / 1000}MHz)");
            }
        }
    }

    private string Kernel(string path, string? macName)
    {
        if (!_files.IsLinux && OperatingSystem.IsMacOS() && macName is not null) return ZabbixItemSet.Integer(ZabbixMacCalls.Int32(macName));
        if (!_files.IsLinux) throw ZabbixItemException.NotOnThisSystem();
        return ZabbixItemSet.Integer(_files.ReadNumber(path, $"Cannot obtain data from {path}."));
    }

    // vm_memory_size (linux/memory.c): sysinfo()'s figures are /proc/meminfo's MemTotal, MemFree and Buffers.
    private string MemorySize(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 1);
        if (!_files.IsLinux && OperatingSystem.IsWindows()) return WindowsMemorySize(key);
        if (!_files.IsLinux && OperatingSystem.IsMacOS()) return MacMemorySize(key);
        string mode = ZabbixItemSet.Mode(key, 0, "total", "free", "buffers", "used", "pused", "available", "pavailable", "shared", "cached", "active", "anon", "inactive", "slab");
        if (!_files.IsLinux) throw ZabbixItemException.NotOnThisSystem();
        if (mode == "shared") throw new ZabbixItemException("This kernel does not report shared memory this way.");
        string[] meminfo = _files.ReadLines("/proc/meminfo");
        ulong Field(string name) => ZabbixHostFiles.KiloBytes(meminfo, name) ?? throw new ZabbixItemException("/proc/meminfo does not have this figure.");
        ulong Available() => ZabbixHostFiles.KiloBytes(meminfo, "MemAvailable:")
            ?? (Field("MemFree:") + Field("Buffers:") + (ZabbixHostFiles.KiloBytes(meminfo, "Cached:") ?? 0));
        return mode switch
        {
            "total" => ZabbixItemSet.Integer(Field("MemTotal:")),
            "free" => ZabbixItemSet.Integer(Field("MemFree:")),
            "buffers" => ZabbixItemSet.Integer(Field("Buffers:")),
            "used" => ZabbixItemSet.Integer(Field("MemTotal:") - Field("MemFree:")),
            "pused" => ZabbixItemSet.Percent(Field("MemTotal:") - Field("MemFree:"), Field("MemTotal:")),
            "available" => ZabbixItemSet.Integer(Available()),
            "pavailable" => ZabbixItemSet.Percent(Available(), Field("MemTotal:")),
            "cached" => ZabbixItemSet.Integer(Field("Cached:")),
            "active" => ZabbixItemSet.Integer(Field("Active:")),
            "anon" => ZabbixItemSet.Integer(Field("AnonPages:")),
            "inactive" => ZabbixItemSet.Integer(Field("Inactive:")),
            _ => ZabbixItemSet.Integer(Field("Slab:")),
        };
    }

    // vm_memory_size (win32/memory.c): free and available are the same figure there.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string WindowsMemorySize(ZabbixItemKey key)
    {
        string mode = ZabbixItemSet.Mode(key, 0, "total", "free", "used", "pused", "available", "pavailable", "cached");
        if (mode == "cached") return ZabbixItemSet.Integer(ZabbixWindowsCalls.CachedBytes());
        ZabbixWindowsCalls.MemoryStatus memory = ZabbixWindowsCalls.Memory();
        ulong used = memory.TotalPhysical - memory.AvailablePhysical;
        return mode switch
        {
            "total" => ZabbixItemSet.Integer(memory.TotalPhysical),
            "free" or "available" => ZabbixItemSet.Integer(memory.AvailablePhysical),
            "used" => ZabbixItemSet.Integer(used),
            "pused" when memory.TotalPhysical != 0 => ZabbixItemSet.Percent(used, memory.TotalPhysical),
            "pavailable" when memory.TotalPhysical != 0 => ZabbixItemSet.Percent(memory.AvailablePhysical, memory.TotalPhysical),
            _ => throw ZabbixItemException.InvalidParameter(0),
        };
    }

    // vm_memory_size (osx/memory.c): used is active and wired, available is inactive and free.
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static string MacMemorySize(ZabbixItemKey key)
    {
        string mode = ZabbixItemSet.Mode(key, 0, "total", "active", "inactive", "wired", "free", "used", "pused", "available", "pavailable");
        if (mode == "total") return ZabbixItemSet.Integer(ZabbixMacCalls.UInt64("hw.memsize"));
        ulong page = (ulong)Environment.SystemPageSize;
        ZabbixMacCalls.Pages pages = ZabbixMacCalls.MemoryPages();
        ulong used = (pages.Active + pages.Wired) * page, available = (pages.Inactive + pages.Free) * page;
        if (mode is not ("pused" or "pavailable"))
        {
            return ZabbixItemSet.Integer(mode switch
            {
                "active" => pages.Active * page,
                "inactive" => pages.Inactive * page,
                "wired" => pages.Wired * page,
                "free" => pages.Free * page,
                "used" => used,
                _ => available,
            });
        }
        ulong total = ZabbixMacCalls.UInt64("hw.memsize");
        return total == 0
            ? throw new ZabbixItemException("The total is zero, so there is no percentage of it.")
            : ZabbixItemSet.Float((mode == "pused" ? used : available) / (double)total * 100);
    }

    // vm_vmemory_size (win32/swap.c): the commit limit and what is left of it.
    private static string VirtualMemorySize(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 1);
        string mode = ZabbixItemSet.Mode(key, 0, "total", "used", "available", "pavailable", "pused");
        if (!OperatingSystem.IsWindows()) throw ZabbixItemException.NotOnThisSystem();
        ZabbixWindowsCalls.MemoryStatus memory = ZabbixWindowsCalls.Memory();
        ulong total = memory.TotalPageFile, available = memory.AvailablePageFile;
        return mode switch
        {
            "total" => ZabbixItemSet.Integer(total),
            "used" => ZabbixItemSet.Integer(total - available),
            "available" => ZabbixItemSet.Integer(available),
            "pavailable" => ZabbixItemSet.Float(available / (double)total * 100),
            _ => ZabbixItemSet.Float((total - available) / (double)total * 100),
        };
    }
}
