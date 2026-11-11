// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.RegularExpressions;

namespace Community.Zabbix.Items;

/// <summary>
/// One process (or thread) as <c>/proc/&lt;pid&gt;</c> describes it. Only <c>status</c> is read at once: the command line,
/// <c>stat</c> and <c>smaps_rollup</c> (which the kernel walks the process's memory to answer) are read when asked for,
/// so a key that matches by name reads what it needs of every process and no more.
/// </summary>
internal sealed class ZabbixLinuxProcess
{
    /// <summary><c>USER_HZ</c>, the unit of <c>stat</c>'s times: 100 on every architecture Linux builds for userspace.</summary>
    public const int TicksPerSecond = 100;

    private readonly ZabbixHostFiles _files;
    private readonly string _directory;
    private readonly string[] _status;
    private string[]? _arguments;
    private string[]? _stat;

    private ZabbixLinuxProcess(ZabbixHostFiles files, string directory, int pid, string status)
    {
        _files = files;
        _directory = directory;
        Pid = pid;
        _status = status.Split('\n');
        Name = Text("Name:") ?? string.Empty;
        string? state = Text("State:");
        State = state is { Length: > 0 } ? state[0] : null;
        int open = state?.IndexOf('(', StringComparison.Ordinal) ?? -1;
        StateName = state is null ? string.Empty : open >= 0 ? state[(open + 1)..].TrimEnd(')') : state;
    }

    public int Pid { get; }

    public string Name { get; }

    public char? State { get; }

    public string StateName { get; }

    public string[] Arguments => _arguments ??= _files.TryReadAllText(_directory + "/cmdline")?.Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? [];

    public string CommandLine => string.Join(' ', Arguments);

    public ulong? Pss => _files.TryReadAllText(_directory + "/smaps_rollup") is { } rollup ? ZabbixHostFiles.KiloBytes(rollup.Split('\n'), "Pss:") : null;

    // size: VmData + VmStk + VmExe, as proc.mem's "size" adds them.
    public ulong? Size => Field("VmData:") is { } data && Field("VmStk:") is { } stack && Field("VmExe:") is { } exe ? data + stack + exe : null;

    public ulong? ContextSwitches => Field("voluntary_ctxt_switches:", bytes: false) is { } voluntary && Field("nonvoluntary_ctxt_switches:", bytes: false) is { } forced
        ? voluntary + forced
        : null;

    // /proc/<pid>/stat after the name: state(3) ... majflt(12) ... utime(14) stime(15) ... starttime(22).
    public ulong? PageFaults => StatField(12);

    public ulong? UserTicks => StatField(14);

    public ulong? SystemTicks => StatField(15);

    public ulong? StartTicks => StatField(22);

    public double UserTime => UserTicks is { } ticks ? ticks / (double)TicksPerSecond : -1.0;

    public double SystemTime => SystemTicks is { } ticks ? ticks / (double)TicksPerSecond : -1.0;

    /// <summary>Every process in <c>/proc</c>; one that goes while it is read is left out.</summary>
    public static IEnumerable<ZabbixLinuxProcess> All(ZabbixHostFiles files)
    {
        foreach (string directory in files.Directories("/proc"))
        {
            if (!int.TryParse(directory, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid == 0) continue;
            if (Read(files, $"/proc/{directory}", pid) is { } process) yield return process;
        }
    }

    /// <summary>The account's id, from the local account database (<c>getpwnam</c>); <see langword="null"/> when it has none.</summary>
    public static ulong? UserId(ZabbixHostFiles files, string user)
    {
        foreach (string line in files.TryReadAllText("/etc/passwd")?.Split('\n') ?? [])
        {
            string[] fields = line.Split(':');
            if (fields.Length > 2 && fields[0] == user && ulong.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong uid)) return uid;
        }
        return null;
    }

    public IEnumerable<ZabbixLinuxProcess> Threads()
    {
        foreach (string task in _files.Directories(_directory + "/task"))
        {
            if (int.TryParse(task, NumberStyles.None, CultureInfo.InvariantCulture, out int tid) && Read(_files, $"{_directory}/task/{task}", tid) is { } thread)
                yield return thread;
        }
    }

    // check_procname, check_user, check_proccomm.
    public bool Matches(string name, ulong? uid, Regex? command)
    {
        if (name.Length > 0 && Name != name && (Arguments.Length == 0 || Path.GetFileName(Arguments[0]) != name)) return false;
        if (uid is not null && Id("Uid:") != uid) return false;
        return command is null || command.IsMatch(CommandLine);
    }

    public ulong? Field(string name, bool bytes = true)
        => bytes ? ZabbixHostFiles.KiloBytes(_status, name) : Text(name) is { } text && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) ? value : null;

    public ulong? Id(string name)
        => Text(name)?.Split('\t', ' ')[0] is { } id && ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) ? value : null;

    private static ZabbixLinuxProcess? Read(ZabbixHostFiles files, string directory, int pid)
        => files.TryReadAllText(directory + "/status") is { } status ? new ZabbixLinuxProcess(files, directory, pid, status) : null;

    private string? Text(string name)
    {
        foreach (string line in _status)
        {
            if (line.StartsWith(name, StringComparison.Ordinal)) return line[name.Length..].Trim();
        }
        return null;
    }

    private ulong? StatField(int field)
    {
        if (_stat is null)
        {
            string stat = _files.TryReadAllText(_directory + "/stat") ?? string.Empty;
            int close = stat.LastIndexOf(')');
            _stat = close < 0 ? [] : stat[(close + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);   // spaces, and the closing newline
        }
        return field - 3 < _stat.Length && ulong.TryParse(_stat[field - 3], NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) ? value : null;
    }
}
