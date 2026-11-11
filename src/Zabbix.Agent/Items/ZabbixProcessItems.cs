// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Community.Zabbix.Items;

/// <summary>
/// Provides Zabbix's process keys: how many processes match, the memory and processor time they use, and their details
/// (<c>proc.num</c>, <c>proc.mem</c>, <c>proc.cpu.util</c>, <c>proc.get</c>, and <c>proc_info</c> on Windows), matched as
/// Zabbix's agent matches them: by name, user, state and command line.
/// </summary>
/// <remarks>
/// On Linux every parameter is answered from <c>/proc</c>, as Zabbix's agent reads it. On Windows and macOS the
/// processes are the .NET <see cref="Process"/> list: <c>proc.num</c>, <c>proc.mem</c> and <c>proc.cpu.util</c> match by
/// name only, and a user, state or command line filter is not supported. <c>proc.cpu.util</c> samples once a second from
/// its first request, which waits for two samples within the check's deadline.
/// </remarks>
public sealed class ZabbixProcessItems : ZabbixItemProvider, IDisposable
{
    private readonly ZabbixItemSet _set = new();
    private readonly ZabbixItemCollector _collector;
    private readonly ZabbixItemCollector? _owned;
    private readonly ZabbixHostFiles _files;

    /// <summary>The process keys of the host this process runs on.</summary>
    /// <param name="time">The clock <c>proc.cpu.util</c> samples on; the system clock by default.</param>
    public ZabbixProcessItems(TimeProvider? time = null)
        : this(ZabbixHostFiles.Host, time ?? TimeProvider.System)
    {
    }

    internal ZabbixProcessItems(ZabbixHostFiles files, TimeProvider time)
        : this(new ZabbixItemCollector(files, time), owned: true)
    {
    }

    internal ZabbixProcessItems(ZabbixItemCollector collector, bool owned = false)
    {
        _collector = collector;
        _owned = owned ? collector : null;
        _files = collector.Files;
        _set.Add("proc.num", "The number of processes that match", Count, "proc.num[,,run]")
            .Add("proc.mem", "The memory the matching processes use, in bytes", Memory, "proc.mem[,,sum,,rss]")
            .Add("proc.get", "The matching processes and their details, as JSON", Get, "proc.get[,,,summary]")
            .Add("proc.cpu.util", "The processor time the matching processes use, as a percentage of one processor", CpuUtilAsync, "proc.cpu.util[,,total,,avg1]")
            .Add("proc_info", "A Windows process's memory and processor time", Info, "proc_info[explorer.exe,wkset,sum]");
    }

    /// <inheritdoc/>
    public override IReadOnlyList<ZabbixItemDefinition> Items => _set.Items;

    /// <inheritdoc/>
    public override ValueTask<ZabbixAgentAnswer> GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken) => _set.AnswerAsync(key, cancellationToken);

    /// <summary>Stops <c>proc.cpu.util</c>'s sampling.</summary>
    public void Dispose() => _owned?.Dispose();

    // proc_cpu_util (linux/proc.c): name, user, type, command line, mode.
    private async ValueTask<string> CpuUtilAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        ZabbixItemSet.AtMost(key, 5);
        string name = ZabbixItemSet.Parameter(key, 0);
        string user = ZabbixItemSet.Parameter(key, 1);
        string type = ZabbixItemSet.Mode(key, 2, "total", "user", "system");
        Regex? command = ZabbixItemSet.Pattern(key, 3);
        int seconds = ZabbixItemSet.Window(key, 4);
        if (!_files.IsLinux && (user.Length > 0 || command is not null)) throw ZabbixItemException.NotOnThisSystem();
        double percent = await _collector.Processes.UtilizationAsync(name, user, ZabbixItemSet.Parameter(key, 3), type != "system", type != "user", seconds, cancellationToken).ConfigureAwait(false);
        return ZabbixItemSet.Float(percent);
    }

    // proc_num (linux/proc.c): name, user, state, command line.
    private string Count(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 4);
        string name = ZabbixItemSet.Parameter(key, 0);
        string user = ZabbixItemSet.Parameter(key, 1);
        char? state = ZabbixItemSet.Mode(key, 2, "all", "run", "sleep", "zomb", "disk", "trace") switch
        {
            "all" => null,
            "run" => 'R',
            "sleep" => 'S',
            "zomb" => 'Z',
            "disk" => 'D',
            _ => 'T',
        };
        Regex? command = ZabbixItemSet.Pattern(key, 3);
        if (!_files.IsLinux)
        {
            if (user.Length > 0 || state is not null || command is not null) throw ZabbixItemException.NotOnThisSystem();
            List<Process> processes = ManagedProcesses(name);
            foreach (Process process in processes) process.Dispose();
            return ZabbixItemSet.Integer(processes.Count);
        }
        ulong? uid = user.Length > 0 ? ZabbixLinuxProcess.UserId(_files, user) : null;
        if (user.Length > 0 && uid is null) return "0";                                 // a user that does not exist owns none
        int count = 0;
        foreach (ZabbixLinuxProcess process in ZabbixLinuxProcess.All(_files))
        {
            if (process.Matches(name, uid, command) && (state is null || process.State == state)) count++;
        }
        return ZabbixItemSet.Integer(count);
    }

    // proc_mem (linux/proc.c): name, user, mode, command line, memory type.
    private string Memory(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 5);
        string name = ZabbixItemSet.Parameter(key, 0);
        string user = ZabbixItemSet.Parameter(key, 1);
        string mode = ZabbixItemSet.Mode(key, 2, "sum", "avg", "max", "min");
        Regex? command = ZabbixItemSet.Pattern(key, 3);
        string type = ZabbixItemSet.Mode(key, 4, "vsize", "rss", "pmem", "size", "peak", "swap", "lib", "lck", "pin", "hwm", "data", "stk", "exe", "pte");
        var values = new List<double>();
        if (!_files.IsLinux)
        {
            if (user.Length > 0 || command is not null || type is not ("vsize" or "rss")) throw ZabbixItemException.NotOnThisSystem();
            foreach (Process process in ManagedProcesses(name))
            {
                using (process) values.Add(type == "vsize" ? process.VirtualMemorySize64 : process.WorkingSet64);
            }
            return Aggregate(values, mode, percent: false);
        }
        ulong? uid = user.Length > 0 ? ZabbixLinuxProcess.UserId(_files, user) : null;
        if (user.Length > 0 && uid is null) return Aggregate(values, mode, percent: type == "pmem");
        double total = type == "pmem" ? ZabbixHostFiles.KiloBytes(_files.ReadLines("/proc/meminfo"), "MemTotal:") ?? 0 : 0;
        if (type == "pmem" && total == 0) throw new ZabbixItemException("The host reports no memory in total.");
        string field = type switch
        {
            "vsize" => "VmSize:",
            "rss" or "pmem" => "VmRSS:",
            "peak" => "VmPeak:",
            "swap" => "VmSwap:",
            "lib" => "VmLib:",
            "lck" => "VmLck:",
            "pin" => "VmPin:",
            "hwm" => "VmHWM:",
            "data" or "size" => "VmData:",
            "stk" => "VmStk:",
            "exe" => "VmExe:",
            _ => "VmPTE:",
        };
        bool tried = false;
        foreach (ZabbixLinuxProcess process in ZabbixLinuxProcess.All(_files))
        {
            if (!process.Matches(name, uid, command)) continue;
            tried = true;
            ulong? bytes = type == "size" ? process.Size : process.Field(field);
            if (bytes is null) continue;
            values.Add(type == "pmem" ? bytes.Value / total * 100.0 : bytes.Value);
        }
        if (tried && values.Count == 0) throw new ZabbixItemException($"No matching process reports \"{field.TrimEnd(':')}\" memory.");
        return Aggregate(values, mode, percent: type == "pmem");
    }

    private static string Aggregate(List<double> values, string mode, bool percent)
    {
        double value = values.Count == 0 ? 0 : mode switch
        {
            "max" => values.Max(),
            "min" => values.Min(),
            "avg" => values.Average(),
            _ => values.Sum(),
        };
        return percent || mode == "avg" ? ZabbixItemSet.Float(value) : ZabbixItemSet.Integer((ulong)value);
    }

    // proc_get (linux/proc.c): name, user, command line, mode; Linux only.
    private string Get(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 4);
        string name = ZabbixItemSet.Parameter(key, 0);
        string user = ZabbixItemSet.Parameter(key, 1);
        string commandText = ZabbixItemSet.Parameter(key, 2);
        string mode = ZabbixItemSet.Parameter(key, 3);
        if (mode.Length == 0) mode = "process";
        if (mode is not ("process" or "thread") && !(mode == "summary" && commandText.Length == 0)) throw ZabbixItemException.InvalidParameter(3);
        Regex? command = ZabbixItemSet.Pattern(key, 2);
        if (!_files.IsLinux) throw ZabbixItemException.NotOnThisSystem();
        ulong? uid = user.Length > 0 ? ZabbixLinuxProcess.UserId(_files, user) : null;
        List<ZabbixLinuxProcess> matched = user.Length > 0 && uid is null ? [] : [.. ZabbixLinuxProcess.All(_files).Where(process => process.Matches(name, uid, command))];
        double total = ZabbixHostFiles.KiloBytes(_files.ReadLines("/proc/meminfo"), "MemTotal:") ?? 0;
        Dictionary<ulong, string> users = Names("/etc/passwd"), groups = Names("/etc/group");
        return ZabbixItemSet.Json(json =>
        {
            json.WriteStartArray();
            if (mode == "summary")
            {
                foreach (IGrouping<string, ZabbixLinuxProcess> group in matched.GroupBy(process => process.Name, StringComparer.Ordinal))
                    WriteSummary(json, group, total);
            }
            else
            {
                foreach (ZabbixLinuxProcess process in matched)
                {
                    if (mode == "process")
                    {
                        WriteProcess(json, process, total, users, groups);
                        continue;
                    }
                    foreach (ZabbixLinuxProcess thread in process.Threads()) WriteThread(json, process, thread, users, groups);
                }
            }
            json.WriteEndArray();
        });
    }

    private static void WriteProcess(Utf8JsonWriter json, ZabbixLinuxProcess process, double total, Dictionary<ulong, string> users, Dictionary<ulong, string> groups)
    {
        json.WriteStartObject();
        json.WriteNumber("pid", process.Pid);
        Number(json, "ppid", process.Field("PPid:", bytes: false));
        json.WriteString("name", process.Name);
        json.WriteString("cmdline", process.CommandLine);
        WriteIdentity(json, process, users, groups);
        Number(json, "vsize", process.Field("VmSize:"));
        ZabbixItemSet.WriteFloat(json, "pmem", Pmem(process, total));
        Number(json, "rss", process.Field("VmRSS:"));
        foreach ((string name, string field) in s_memoryFields) Number(json, name, process.Field(field));
        Number(json, "size", process.Size);
        Number(json, "stk", process.Field("VmStk:"));
        Number(json, "swap", process.Field("VmSwap:"));
        ZabbixItemSet.WriteFloat(json, "cputime_user", process.UserTime);
        ZabbixItemSet.WriteFloat(json, "cputime_system", process.SystemTime);
        json.WriteString("state", process.StateName);
        Number(json, "ctx_switches", process.ContextSwitches);
        Number(json, "threads", process.Field("Threads:", bytes: false));
        Number(json, "page_faults", process.PageFaults);
        Number(json, "pss", process.Pss);
        json.WriteEndObject();
    }

    private static readonly System.Collections.Immutable.ImmutableArray<(string Name, string Field)> s_memoryFields =
        [("data", "VmData:"), ("exe", "VmExe:"), ("hwm", "VmHWM:"), ("lck", "VmLck:"), ("lib", "VmLib:"), ("peak", "VmPeak:"), ("pin", "VmPin:"), ("pte", "VmPTE:")];

    private static void WriteThread(Utf8JsonWriter json, ZabbixLinuxProcess process, ZabbixLinuxProcess thread, Dictionary<ulong, string> users, Dictionary<ulong, string> groups)
    {
        json.WriteStartObject();
        json.WriteNumber("pid", process.Pid);
        Number(json, "ppid", process.Field("PPid:", bytes: false));
        json.WriteString("name", process.Name);
        WriteIdentity(json, process, users, groups);
        json.WriteNumber("tid", thread.Pid);
        json.WriteString("tname", thread.Name);
        ZabbixItemSet.WriteFloat(json, "cputime_user", thread.UserTime);
        ZabbixItemSet.WriteFloat(json, "cputime_system", thread.SystemTime);
        json.WriteString("state", thread.StateName);
        Number(json, "ctx_switches", thread.ContextSwitches);
        Number(json, "page_faults", thread.PageFaults);
        json.WriteEndObject();
    }

    private static void WriteSummary(Utf8JsonWriter json, IGrouping<string, ZabbixLinuxProcess> group, double total)
    {
        json.WriteStartObject();
        json.WriteString("name", group.Key);
        json.WriteNumber("processes", group.Count());
        Number(json, "vsize", Sum(group, process => process.Field("VmSize:")));
        ZabbixItemSet.WriteFloat(json, "pmem", group.Sum(process => Pmem(process, total)));
        Number(json, "rss", Sum(group, process => process.Field("VmRSS:")));
        foreach ((string name, string field) in s_memoryFields.Where(entry => entry.Name is not ("hwm" or "peak"))) Number(json, name, Sum(group, process => process.Field(field)));
        Number(json, "size", Sum(group, process => process.Size));
        Number(json, "stk", Sum(group, process => process.Field("VmStk:")));
        Number(json, "swap", Sum(group, process => process.Field("VmSwap:")));
        ZabbixItemSet.WriteFloat(json, "cputime_user", group.Sum(process => process.UserTime));
        ZabbixItemSet.WriteFloat(json, "cputime_system", group.Sum(process => process.SystemTime));
        Number(json, "ctx_switches", Sum(group, process => process.ContextSwitches));
        Number(json, "threads", Sum(group, process => process.Field("Threads:", bytes: false)));
        Number(json, "page_faults", Sum(group, process => process.PageFaults));
        Number(json, "pss", Sum(group, process => process.Pss));
        json.WriteEndObject();
    }

    private static void WriteIdentity(Utf8JsonWriter json, ZabbixLinuxProcess process, Dictionary<ulong, string> users, Dictionary<ulong, string> groups)
    {
        ulong? uid = process.Id("Uid:"), gid = process.Id("Gid:");
        json.WriteString("user", uid is { } u && users.TryGetValue(u, out string? user) ? user : string.Empty);
        json.WriteString("group", gid is { } g && groups.TryGetValue(g, out string? group) ? group : string.Empty);
        Number(json, "uid", uid);
        Number(json, "gid", gid);
    }

    // A value that cannot be read is -1 (JSON_ADD_PROC_VALUE).
    private static void Number(Utf8JsonWriter json, string name, ulong? value)
    {
        if (value is { } known) json.WriteNumber(name, known);
        else json.WriteNumber(name, -1);
    }

    private static ulong? Sum(IEnumerable<ZabbixLinuxProcess> processes, Func<ZabbixLinuxProcess, ulong?> value)
    {
        ulong sum = 0;
        foreach (ZabbixLinuxProcess process in processes)
        {
            if (value(process) is not { } known) return null;
            sum += known;
        }
        return sum;
    }

    private static double Pmem(ZabbixLinuxProcess process, double total) => total > 0 && process.Field("VmRSS:") is { } rss ? rss / total * 100 : 0;

    // proc_info (win32/proc.c): every process of that image this account may open; type avg (default), min, max, sum.
    private static string Info(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 3);
        string name = ZabbixItemSet.Parameter(key, 0);
        if (name.Length == 0) throw ZabbixItemException.InvalidParameter(0);
        string attribute = ZabbixItemSet.Parameter(key, 1);
        if (attribute.Length == 0) attribute = "vmsize";
        if (attribute is not ("vmsize" or "wkset" or "pf" or "ktime" or "utime" or "gdiobj" or "userobj"
            or "io_read_b" or "io_read_op" or "io_write_b" or "io_write_op" or "io_other_b" or "io_other_op"))
            throw ZabbixItemException.InvalidParameter(1);
        string type = ZabbixItemSet.Mode(key, 2, "avg", "min", "max", "sum");
        if (!OperatingSystem.IsWindows()) throw ZabbixItemException.NotOnThisSystem();
        var values = new List<double>();
        foreach (Process process in ManagedProcesses(name))
        {
            using (process)
            {
                if (WindowsAttribute(process, attribute) is { } value) values.Add(value);
            }
        }
        return ZabbixItemSet.Float(values.Count == 0 ? 0 : type switch
        {
            "min" => values.Min(),
            "max" => values.Max(),
            "sum" => values.Sum(),
            _ => values.Average(),
        });
    }

    // GetProcessAttribute: a process that has gone or that this account may not open is left out, as OpenProcess leaves it out.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static double? WindowsAttribute(Process process, string attribute)
    {
        using Microsoft.Win32.SafeHandles.SafeProcessHandle? handle = ZabbixWindowsCalls.Query(process.Id);
        if (handle is null) return null;
        switch (attribute)
        {
            case "ktime":
            case "utime":
                try
                {
                    return (attribute == "ktime" ? process.PrivilegedProcessorTime : process.UserProcessorTime).TotalMilliseconds;
                }
                catch (Exception refused) when (refused is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    return null;
                }
            case "gdiobj":
                return ZabbixWindowsCalls.GuiObjects(handle, 0);
            case "userobj":
                return ZabbixWindowsCalls.GuiObjects(handle, 1);
            case "vmsize":
            case "wkset":
            case "pf":
                if (!ZabbixWindowsCalls.TryMemory(handle, out ZabbixWindowsCalls.MemoryCounters memory)) throw new ZabbixItemException("The process's figures cannot be read.");
                return attribute switch
                {
                    "vmsize" => (double)memory.PageFile / 1024,
                    "wkset" => (double)memory.WorkingSet / 1024,
                    _ => memory.PageFaults,
                };
            default:
                if (!ZabbixWindowsCalls.TryIo(handle, out ZabbixWindowsCalls.IoCounters io)) throw new ZabbixItemException("The process's figures cannot be read.");
                return attribute switch
                {
                    "io_read_b" => io.ReadBytes,
                    "io_read_op" => io.ReadOperations,
                    "io_write_b" => io.WriteBytes,
                    "io_write_op" => io.WriteOperations,
                    "io_other_b" => io.OtherBytes,
                    _ => io.OtherOperations,
                };
        }
    }

    // The .NET process list, by name: Windows matches the image name (explorer.exe) as Zabbix's agent does.
    internal static List<Process> ManagedProcesses(string name)
    {
        var matched = new List<Process>();
        foreach (Process process in Process.GetProcesses())
        {
            bool match = name.Length == 0
                || (OperatingSystem.IsWindows()
                    ? string.Equals(process.ProcessName + ".exe", name, StringComparison.OrdinalIgnoreCase) || string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(process.ProcessName, name, StringComparison.Ordinal));
            if (match) matched.Add(process);
            else process.Dispose();
        }
        return matched;
    }

    private Dictionary<ulong, string> Names(string path)
    {
        var names = new Dictionary<ulong, string>();
        foreach (string line in _files.TryReadAllText(path)?.Split('\n') ?? [])
        {
            string[] fields = line.Split(':');
            if (fields.Length > 2 && ulong.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)) names.TryAdd(id, fields[0]);
        }
        return names;
    }
}
