// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;

namespace Community.Zabbix.Items;

/// <summary>
/// The operating system's own files a bundled key reads, Linux's <c>/proc</c> and <c>/sys</c> as Zabbix's agent reads
/// them, under a root: <c>/</c> on a host, a fixture tree in a test. A file that cannot be read is the key's
/// not-supported reason, in Zabbix's words ("/proc/meminfo cannot be opened: …").
/// </summary>
internal sealed class ZabbixHostFiles(string root, bool linux)
{
    /// <summary>The host's own files.</summary>
    public static ZabbixHostFiles Host { get; } = new("/", OperatingSystem.IsLinux());

    /// <summary>Whether the files are Linux's: the host's on Linux, a fixture tree in a test on any system.</summary>
    public bool IsLinux { get; } = linux;

    /// <summary>Where <paramref name="path"/> (absolute, as on the host) is under the root.</summary>
    public string Resolve(string path) => root == "/" ? path : Path.Join(root, path);

    /// <summary>What a link at <paramref name="path"/> finally points to (<c>/dev/mapper/swap</c> to <c>/dev/dm-1</c>), or <see langword="null"/> when it is not a link or cannot be read.</summary>
    public static string? LinkTarget(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public bool Exists(string path) => File.Exists(Resolve(path));

    public bool DirectoryExists(string path) => Directory.Exists(Resolve(path));

    /// <summary>The file's text; "&lt;path&gt; cannot be opened: &lt;reason&gt;" when it cannot be read.</summary>
    public string ReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(Resolve(path));
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            throw new ZabbixItemException($"{path} cannot be opened: {refused.Message}", refused);
        }
    }

    /// <summary>The file's text, or <see langword="null"/> when it cannot be read.</summary>
    public string? TryReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(Resolve(path));
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The file's lines; "&lt;path&gt; cannot be opened: &lt;reason&gt;" when it cannot be read.</summary>
    public string[] ReadLines(string path) => ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The file's first line, trimmed as Zabbix trims it (<c>zbx_rtrim</c>, whitespace).</summary>
    public string ReadFirstLine(string path)
    {
        string text = ReadAllText(path);
        int end = text.IndexOf('\n', StringComparison.Ordinal);
        return (end < 0 ? text : text[..end]).TrimEnd();
    }

    /// <summary>The unsigned integer that starts the file's first line.</summary>
    public ulong ReadNumber(string path, string failure)
    {
        string? text = TryReadAllText(path);
        if (text is null) throw new ZabbixItemException(failure);
        int end = 0;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        return end > 0 && ulong.TryParse(text.AsSpan(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out ulong value)
            ? value
            : throw new ZabbixItemException(failure);
    }

    /// <summary>The directories directly under <paramref name="path"/>, by name; none when it cannot be read.</summary>
    public IEnumerable<string> Directories(string path)
    {
        string resolved = Resolve(path);
        return Directory.Exists(resolved) ? Directory.EnumerateDirectories(resolved).Select(directory => Path.GetFileName(directory)) : [];
    }

    /// <summary>
    /// A value from a <c>/proc/meminfo</c>-style file, in bytes: the line that starts with <paramref name="name"/>
    /// (<c>MemTotal:</c>), its number in kB (<c>byte_value_from_proc_file</c>). <see langword="null"/> when no line has it.
    /// </summary>
    public static ulong? KiloBytes(string[] lines, string name)
    {
        foreach (string line in lines)
        {
            if (!line.StartsWith(name, StringComparison.Ordinal)) continue;
            ReadOnlySpan<char> rest = line.AsSpan(name.Length).Trim();
            int end = 0;
            while (end < rest.Length && char.IsAsciiDigit(rest[end])) end++;
            if (end == 0 || !ulong.TryParse(rest[..end], NumberStyles.None, CultureInfo.InvariantCulture, out ulong value)) return null;
            return rest[end..].Trim().Equals("kB", StringComparison.Ordinal) ? value * 1024 : value;
        }
        return null;
    }

    /// <summary>The count a <c>/sys</c> CPU list names (<c>0-3,5,7-8</c> is seven).</summary>
    public static int CountCpuList(string list)
    {
        int count = 0;
        foreach (string range in list.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = range.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0)
            {
                count++;
                continue;
            }
            int first = int.Parse(range.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture);
            int last = int.Parse(range.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture);
            count += last - first + 1;
        }
        return count;
    }

    /// <summary>The numbers a <c>/sys</c> CPU list names, in order.</summary>
    public static IEnumerable<int> CpuList(string list)
    {
        foreach (string range in list.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = range.IndexOf('-', StringComparison.Ordinal);
            int first = int.Parse(dash < 0 ? range : range[..dash], NumberStyles.None, CultureInfo.InvariantCulture);
            int last = dash < 0 ? first : int.Parse(range.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture);
            for (int cpu = first; cpu <= last; cpu++) yield return cpu;
        }
    }
}
