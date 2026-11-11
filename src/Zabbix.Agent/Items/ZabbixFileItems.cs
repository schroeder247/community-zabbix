// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Community.Zabbix.Items;

/// <summary>
/// Provides Zabbix's file and directory keys: whether a file exists, its size, times, owner, contents, permissions and
/// checksums, the lines that match a regular expression, and what a directory holds (<c>vfs.file.*</c>,
/// <c>vfs.dir.*</c>), with Zabbix's parameters.
/// </summary>
/// <remarks>
/// The keys read files with the agent's own account, as Zabbix's agent does; a file it may not read is not supported,
/// with the operating system's reason. Regular expressions are .NET's (<see cref="Regex"/>), which accept the patterns
/// Zabbix's documentation gives. On Linux and macOS a file's type, owner and change time are <c>stat</c>'s; on Windows
/// the owner is the file's security descriptor's, and the types are a file, a directory and a directory link.
/// </remarks>
public sealed class ZabbixFileItems : ZabbixItemProvider
{
    // ZBX_MAX_VFS_FILE_SIZE: vfs.file.contents refuses a larger file.
    private const long MaxContents = 16 * 1024 * 1024;
    private static readonly System.Collections.Immutable.ImmutableArray<uint> s_cksumTable = CksumTable();

    // Every entry but "." and "..", and a directory that cannot be read says so.
    private static readonly EnumerationOptions s_oneDirectory = new() { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };

    private readonly ZabbixItemSet _set = new();
    private readonly TimeProvider _time;

    /// <summary>The file keys of the host this process runs on.</summary>
    /// <param name="time">The clock <c>vfs.dir.count</c>'s and <c>vfs.dir.get</c>'s age filters read, and the time zone <c>vfs.file.get</c> writes its times in; the system's by default.</param>
    public ZabbixFileItems(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _set.Add("vfs.file.exists", "Whether a file exists", Exists, "vfs.file.exists[/etc/passwd]")
            .Add("vfs.file.size", "A file's size, in bytes or lines", SizeAsync, "vfs.file.size[/etc/passwd]")
            .Add("vfs.file.time", "When a file was last modified, read or changed, in seconds since the epoch", FileTime, "vfs.file.time[/etc/passwd,modify]")
            .Add("vfs.file.owner", "A file's owner, as a name or an id", Owner, "vfs.file.owner[/etc/passwd,user,name]")
            .Add("vfs.file.get", "A file's type, owner, permissions, size and times, as JSON", Get, "vfs.file.get[/etc/passwd]")
            .Add("vfs.file.contents", "A file's contents", ContentsAsync, "vfs.file.contents[/etc/hostname]")
            .Add("vfs.file.regexp", "The first line of a file that matches a regular expression", RegexpAsync, "vfs.file.regexp[/etc/passwd,^root]")
            .Add("vfs.file.regmatch", "Whether any line of a file matches a regular expression", RegmatchAsync, "vfs.file.regmatch[/etc/passwd,^root]")
            .Add("vfs.file.md5sum", "A file's MD5 checksum", Md5Async, "vfs.file.md5sum[/etc/passwd]")
            .Add("vfs.file.cksum", "A file's checksum: the UNIX cksum, MD5 or SHA-256", CksumAsync, "vfs.file.cksum[/etc/passwd,sha256]")
            .Add("vfs.file.permissions", "A file's UNIX permissions, as four octal digits", Permissions, "vfs.file.permissions[/etc/passwd]")
            .Add("vfs.dir.count", "The number of entries in a directory that match", (key, token) => new ValueTask<string>(DirectoryCount(key, token)), "vfs.dir.count[/var/log]")
            .Add("vfs.dir.get", "The entries in a directory that match, as JSON", (key, token) => new ValueTask<string>(DirectoryGet(key, token)), "vfs.dir.get[/var/log]")
            .Add("vfs.dir.size", "The size of what a directory holds", (key, token) => new ValueTask<string>(DirectorySize(key, token)), "vfs.dir.size[/var/log]");
    }

    /// <inheritdoc/>
    public override IReadOnlyList<ZabbixItemDefinition> Items => _set.Items;

    /// <inheritdoc/>
    public override ValueTask<ZabbixAgentAnswer> GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken = default) => _set.AnswerAsync(key, cancellationToken);

    private static string FileName(ZabbixItemKey key, int parameters)
    {
        ZabbixItemSet.AtMost(key, parameters);
        string name = ZabbixItemSet.Parameter(key, 0);
        return name.Length == 0 ? throw ZabbixItemException.InvalidParameter(0) : name;
    }

    // vfs_file_exists_local: the types the path is, against the included and excluded ones. A link is itself and its target.
    private static string Exists(ZabbixItemKey key)
    {
        string name = FileName(key, 3);
        ZabbixFileType include = Types(ZabbixItemSet.Parameter(key, 1)), exclude = Types(ZabbixItemSet.Parameter(key, 2));
        if (include == ZabbixFileType.None) include = exclude == ZabbixFileType.None ? ZabbixFileType.File : ZabbixFileType.All;
        if (OperatingSystem.IsWindows()) return WindowsExists(name, include & ~exclude & ZabbixFileType.All) ? "1" : "0";
        ZabbixFileType types = ZabbixFileType.None;
        if (((include | exclude) & ZabbixFileType.Symlink) != 0)
        {
            if (ZabbixUnixCalls.TryStat(name, followLinks: false, out ZabbixFileStat link, out int linkError))
            {
                if (link.Type == ZabbixFileType.Symlink) types |= ZabbixFileType.Symlink;
            }
            else if (linkError != ZabbixUnixCalls.NoSuchEntry)
            {
                throw Unreadable(linkError);
            }
        }
        if (ZabbixUnixCalls.TryStat(name, followLinks: true, out ZabbixFileStat target, out int error)) types |= target.Type;
        else if (error != ZabbixUnixCalls.NoSuchEntry) throw Unreadable(error);
        return (types & exclude) == 0 && (types & include) != 0 ? "1" : "0";
    }

    // A directory link is a link and a directory; a file link counts as a file, as Explorer counts it.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool WindowsExists(string name, ZabbixFileType wanted)
    {
        var info = new FileInfo(name);
        if ((int)info.Attributes == -1) return false;
        bool link = (info.Attributes & FileAttributes.ReparsePoint) != 0, directory = (info.Attributes & FileAttributes.Directory) != 0;
        return link && directory ? (wanted & (ZabbixFileType.Symlink | ZabbixFileType.Directory)) != 0
            : directory ? (wanted & ZabbixFileType.Directory) != 0
            : (wanted & ZabbixFileType.File) != 0;
    }

    private static ZabbixItemException Unreadable(int error, string what = "file") => new($"The {what}'s details cannot be read: {System.Runtime.InteropServices.Marshal.GetPInvokeErrorMessage(error)}");

    // zbx_etypes_to_mask: a comma-separated list; "dev" is both device types, "all" every type.
    private static ZabbixFileType Types(string list)
    {
        ZabbixFileType types = ZabbixFileType.None;
        foreach (string name in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            types |= name.Trim() switch
            {
                "file" => ZabbixFileType.File,
                "dir" => ZabbixFileType.Directory,
                "sym" => ZabbixFileType.Symlink,
                "sock" => ZabbixFileType.Socket,
                "bdev" => ZabbixFileType.Block,
                "cdev" => ZabbixFileType.Character,
                "fifo" => ZabbixFileType.Fifo,
                "dev" => ZabbixFileType.Block | ZabbixFileType.Character,
                "all" => ZabbixFileType.All,
                string other => throw new ZabbixItemException($"\"{other}\" is not a file type."),
            };
        }
        return types;
    }

    private static async ValueTask<string> SizeAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        string name = FileName(key, 2);
        string mode = ZabbixItemSet.Mode(key, 1, "bytes", "lines");
        if (mode == "bytes") return ZabbixItemSet.Integer(Info(name).Length);
        // The newlines in the file: an empty line counts, a last line without one does not.
        FileStream file = Open(name);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable closing = file.ConfigureAwait(false);
        byte[] buffer = new byte[65536];
        ulong lines = 0;
        int read;
        while ((read = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0) lines += (ulong)buffer.AsSpan(0, read).Count((byte)'\n');
        return ZabbixItemSet.Integer(lines);
    }

    private static string FileTime(ZabbixItemKey key)
    {
        string name = FileName(key, 2);
        string mode = ZabbixItemSet.Mode(key, 1, "modify", "access", "change");
        if (!OperatingSystem.IsWindows())
        {
            if (!ZabbixUnixCalls.TryStat(name, followLinks: true, out ZabbixFileStat stat, out int error)) throw Unreadable(error);
            return ZabbixItemSet.Integer(mode switch { "modify" => stat.Modified, "access" => stat.Accessed, _ => stat.Changed });
        }
        FileInfo info = Info(name);
        if (mode == "change") return ZabbixItemSet.Integer(ZabbixWindowsCalls.ChangeTime(name, followLinks: true));
        DateTimeOffset time = mode == "modify" ? info.LastWriteTimeUtc : info.LastAccessTimeUtc;
        return ZabbixItemSet.Integer(time.ToUnixTimeSeconds());
    }

    private static async ValueTask<string> ContentsAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        string name = FileName(key, 2);
        Encoding? encoding = Encoding(ZabbixItemSet.Parameter(key, 1), 1);
        if (Info(name).Length > MaxContents) throw TooLarge();
        byte[] bytes = await ReadAsync(name, cancellationToken).ConfigureAwait(false);
        return Decode(bytes, encoding).TrimEnd('\r', '\n');
    }

    // vfs_file_regexp: the first matching line between the start and end lines, through the output template.
    private static async ValueTask<string> RegexpAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        (string name, Regex pattern, Encoding? encoding, int first, int last) = LineSearch(key, 6);
        string output = ZabbixItemSet.Parameter(key, 5);
        int number = 0;
        await foreach (string line in LinesAsync(name, encoding, cancellationToken).ConfigureAwait(false))
        {
            if (++number < first) continue;
            Match match = pattern.Match(line);
            if (match.Success) return Substitute(line, match, output);
            if (number >= last) break;
        }
        return string.Empty;
    }

    private static async ValueTask<string> RegmatchAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        (string name, Regex pattern, Encoding? encoding, int first, int last) = LineSearch(key, 5);
        int number = 0;
        await foreach (string line in LinesAsync(name, encoding, cancellationToken).ConfigureAwait(false))
        {
            if (++number < first) continue;
            if (pattern.IsMatch(line)) return "1";
            if (number >= last) break;
        }
        return "0";
    }

    private static (string Name, Regex Pattern, Encoding? Encoding, int First, int Last) LineSearch(ZabbixItemKey key, int parameters)
    {
        string name = FileName(key, parameters);
        string text = ZabbixItemSet.Parameter(key, 1);
        if (text.Length == 0) throw ZabbixItemException.InvalidParameter(1);
        Regex pattern = ZabbixItemSet.Expression(text, 1);
        Encoding? encoding = Encoding(ZabbixItemSet.Parameter(key, 2), 2);
        int first = LineNumber(key, 3, 1), last = LineNumber(key, 4, int.MaxValue);
        if (first > last) throw new ZabbixItemException("The start line comes after the end line.");
        return (name, pattern, encoding, first, last);
    }

    private static int LineNumber(ZabbixItemKey key, int index, int absent)
    {
        string text = ZabbixItemSet.Parameter(key, index);
        if (text.Length == 0) return absent;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int line) && line > 0 ? line : throw ZabbixItemException.InvalidParameter(index);
    }

    // zbx_regexp_sub: no template answers the whole line; \0 is the match, \1 to \9 its groups (empty when absent).
    internal static string Substitute(string line, Match match, string output)
    {
        if (output.Length == 0) return line;
        var result = new StringBuilder();
        for (int i = 0; i < output.Length; i++)
        {
            if (output[i] == '\\' && i + 1 < output.Length && char.IsAsciiDigit(output[i + 1]))
            {
                int group = output[++i] - '0';
                if (group < match.Groups.Count && match.Groups[group].Success) result.Append(match.Groups[group].Value);
                continue;
            }
            result.Append(output[i]);
        }
        return result.ToString();
    }

    private static async ValueTask<string> Md5Async(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        string name = FileName(key, 1);
        FileStream file = Open(name);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable closing = file.ConfigureAwait(false);
#pragma warning disable CA5351 // vfs.file.md5sum is Zabbix's MD5 checksum, compared with other hosts' and with packages' sums, not a security control
        return Convert.ToHexStringLower(await MD5.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
#pragma warning restore CA5351
    }

    private static async ValueTask<string> CksumAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        string name = FileName(key, 2);
        string mode = ZabbixItemSet.Mode(key, 1, "crc32", "md5", "sha256");
        FileStream file = Open(name);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable closing = file.ConfigureAwait(false);
        switch (mode)
        {
            case "md5":
#pragma warning disable CA5351 // as vfs.file.md5sum
                return Convert.ToHexStringLower(await MD5.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
#pragma warning restore CA5351
            case "sha256":
                return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
            default:
                return ZabbixItemSet.Integer(await PosixCksumAsync(file, cancellationToken).ConfigureAwait(false));
        }
    }

    // The POSIX cksum: CRC-32 (polynomial 0x04C11DB7, most significant bit first) over the data and then its length,
    // least significant byte first, complemented. System.IO.Hashing.Crc32 is zlib's reflected CRC, another sum.
    internal static async ValueTask<uint> PosixCksumAsync(Stream data, CancellationToken cancellationToken)
    {
        uint crc = 0;
        long length = 0;
        byte[] buffer = new byte[65536];
        int read;
        while ((read = await data.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            foreach (byte b in buffer.AsSpan(0, read)) crc = (crc << 8) ^ s_cksumTable[(int)((crc >> 24) ^ b)];
            length += read;
        }
        for (long n = length; n != 0; n >>= 8) crc = (crc << 8) ^ s_cksumTable[(int)((crc >> 24) ^ (byte)(n & 0xFF))];
        return ~crc;
    }

    private static System.Collections.Immutable.ImmutableArray<uint> CksumTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int bit = 0; bit < 8; bit++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            table[i] = c;
        }
        return [.. table];
    }

    private static string Permissions(ZabbixItemKey key)
    {
        string name = FileName(key, 1);
        if (OperatingSystem.IsWindows()) throw new ZabbixItemException("Windows has no UNIX permissions to report.");
        try
        {
            int mode = (int)File.GetUnixFileMode(name);
            return Convert.ToString(mode, 8).PadLeft(4, '0');
        }
        catch (FileNotFoundException missing)
        {
            throw new ZabbixItemException($"The file's details cannot be read: {missing.Message}", missing);
        }
    }

    // vfs_file_owner: Windows has only the user, and its id is a SID; Unix says the id when an account has no name.
    private static string Owner(ZabbixItemKey key)
    {
        string name = FileName(key, 3);
        string owner = ZabbixItemSet.Parameter(key, 1), result = ZabbixItemSet.Parameter(key, 2);
        if (OperatingSystem.IsWindows())
        {
            if (owner is not ("" or "user")) throw ZabbixItemException.InvalidParameter(1);
            if (result is not ("" or "id" or "name")) throw ZabbixItemException.InvalidParameter(2);
            (string? account, string sid) = WindowsOwner(name);
            return result == "id" ? sid : account ?? throw new ZabbixItemException("The owner's account name cannot be read.");
        }
        if (!ZabbixUnixCalls.TryStat(name, followLinks: false, out ZabbixFileStat stat, out int error)) throw Unreadable(error);
        bool group = owner == "group";
        if (!group && owner is not ("" or "user")) throw ZabbixItemException.InvalidParameter(1);
        uint id = group ? stat.Group : stat.User;
        return result switch
        {
            "id" => ZabbixItemSet.Integer(id),
            "" or "name" => (group ? ZabbixUnixCalls.GroupName(id) : ZabbixUnixCalls.UserName(id)) ?? ZabbixItemSet.Integer(id),
            _ => throw ZabbixItemException.InvalidParameter(2),
        };
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (string? Account, string Sid) WindowsOwner(string name)
    {
        try
        {
            System.Security.AccessControl.FileSystemSecurity security = Directory.Exists(name)
                ? new DirectoryInfo(name).GetAccessControl(System.Security.AccessControl.AccessControlSections.Owner)
                : new FileInfo(name).GetAccessControl(System.Security.AccessControl.AccessControlSections.Owner);
            System.Security.Principal.IdentityReference sid = security.GetOwner(typeof(System.Security.Principal.SecurityIdentifier))
                ?? throw new ZabbixItemException("The file's owner cannot be read.");
            string? account;
            try
            {
                account = sid.Translate(typeof(System.Security.Principal.NTAccount)).Value;
            }
            catch (System.Security.Principal.IdentityNotMappedException)
            {
                account = null;
            }
            return (account, sid.Value);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new ZabbixItemException("The file's owner cannot be read.", refused);
        }
    }

    private string Get(ZabbixItemKey key)
    {
        string name = FileName(key, 1);
        return ZabbixItemSet.Json(json => WriteFile(json, Details(name, new Dictionary<uint, string>(), new Dictionary<uint, string>())));
    }

    // What zbx_vfs_file_info writes: a Unix file's user, group, permissions and ids, or a Windows file's user and SID.
    private readonly record struct FileDetails(
        string BaseName,
        string PathName,
        string DirectoryName,
        string? Type,
        string? User,
        string? Group,
        string? Permissions,
        uint UserId,
        uint GroupId,
        string? OwnerSecurityId,
        ulong Size,
        long Accessed,
        long Modified,
        long Changed);

    private static FileDetails Details(string path, Dictionary<uint, string> users, Dictionary<uint, string> groups)
    {
        string full = Path.GetFullPath(path);
        string trimmed = Path.TrimEndingDirectorySeparator(full);
        string baseName = Path.GetFileName(trimmed) is { Length: > 0 } last ? last : full;
        string directoryName = Path.GetDirectoryName(trimmed) ?? full;
        if (OperatingSystem.IsWindows()) return WindowsDetails(path, trimmed, baseName, directoryName);
        if (!ZabbixUnixCalls.TryStat(path, followLinks: false, out ZabbixFileStat stat, out int error)) throw Unreadable(error);
        string type = stat.Type switch
        {
            ZabbixFileType.Symlink => "sym",
            ZabbixFileType.File => "file",
            ZabbixFileType.Directory => "dir",
            ZabbixFileType.Socket => "sock",
            ZabbixFileType.Block => "bdev",
            ZabbixFileType.Character => "cdev",
            ZabbixFileType.Fifo => "fifo",
            _ => "unknown",
        };
        return new FileDetails(
            baseName,
            trimmed,
            directoryName,
            type,
            AccountName(users, stat.User, ZabbixUnixCalls.UserName),
            AccountName(groups, stat.Group, ZabbixUnixCalls.GroupName),
            Convert.ToString(stat.Permissions, 8).PadLeft(4, '0'),
            stat.User,
            stat.Group,
            OwnerSecurityId: null,
            stat.Size,
            stat.Accessed,
            stat.Modified,
            stat.Changed);
    }

    // A directory's owners rarely differ, so each is asked about once per answer.
    private static string AccountName(Dictionary<uint, string> known, uint id, Func<uint, string?> lookup)
    {
        if (known.TryGetValue(id, out string? name)) return name;
        name = lookup(id) ?? ZabbixItemSet.Integer(id);
        known[id] = name;
        return name;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static FileDetails WindowsDetails(string path, string full, string baseName, string directoryName)
    {
        var info = new FileInfo(path);
        if ((int)info.Attributes == -1) throw new ZabbixItemException($"The file's details cannot be read: {path} does not exist.");
        bool link = (info.Attributes & FileAttributes.ReparsePoint) != 0, directory = (info.Attributes & FileAttributes.Directory) != 0;
        (string? account, string sid) = WindowsOwner(path);
        long accessed = 0, modified = 0, changed = 0;
        try
        {
            changed = ZabbixWindowsCalls.ChangeTime(path, followLinks: false);
            accessed = new DateTimeOffset(info.LastAccessTimeUtc).ToUnixTimeSeconds();
            modified = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
        }
        catch (ZabbixItemException)
        {
            // zbx_vfs_file_info: a file whose times cannot be read is reported with none.
        }
        return new FileDetails(
            baseName,
            full,
            directoryName,
            link ? "sym" : directory ? "dir" : "file",
            account,
            Group: null,
            Permissions: null,
            UserId: 0,
            GroupId: 0,
            sid,
            link || directory ? 0 : (ulong)info.Length,
            accessed,
            modified,
            changed);
    }

    private void WriteFile(System.Text.Json.Utf8JsonWriter json, FileDetails file)
    {
        json.WriteStartObject();
        json.WriteString("basename", file.BaseName);
        json.WriteString("pathname", file.PathName);
        json.WriteString("dirname", file.DirectoryName);
        json.WriteString("type", file.Type);
        json.WriteString("user", file.User);
        if (file.OwnerSecurityId is null)
        {
            json.WriteString("group", file.Group);
            json.WriteString("permissions", file.Permissions);
            json.WriteNumber("uid", file.UserId);
            json.WriteNumber("gid", file.GroupId);
        }
        else
        {
            json.WriteString("SID", file.OwnerSecurityId);
        }
        json.WriteNumber("size", file.Size);
        json.WriteStartObject("time");
        json.WriteString("access", PrintTime(file.Accessed));
        json.WriteString("modify", PrintTime(file.Modified));
        json.WriteString("change", PrintTime(file.Changed));
        json.WriteEndObject();
        json.WriteStartObject("timestamp");
        json.WriteNumber("access", Math.Max(file.Accessed, 0));
        json.WriteNumber("modify", Math.Max(file.Modified, 0));
        json.WriteNumber("change", Math.Max(file.Changed, 0));
        json.WriteEndObject();
        json.WriteEndObject();
    }

    // get_print_time: strftime's %Y-%m-%dT%T%z, in this host's time zone.
    private string PrintTime(long seconds)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(Math.Max(seconds, 0)), _time.LocalTimeZone);
        TimeSpan offset = local.Offset;
        return string.Create(CultureInfo.InvariantCulture, $"{local:yyyy-MM-ddTHH:mm:ss}{(offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(offset.Hours):00}{Math.Abs(offset.Minutes):00}");
    }

    // prepare_common_parameters and prepare_count_parameters (common/dir.c), and what they settle between them.
    private readonly record struct DirectoryWalk(string Root, int Depth, Regex? Include, Regex? Exclude, Regex? ExcludeDirectory);

    private readonly record struct DirectoryFilter(ZabbixFileType Types, ulong MinimumSize, ulong MaximumSize, long Oldest, long Newest);

    // One entry the walk found: on Linux and macOS its stat, on Windows what its attributes say. A reparse point to a
    // directory is neither followed nor reported on Windows, and a file link is a file, as Explorer counts them.
    private readonly record struct DirectoryEntry(string Path, string Name, ZabbixFileType Type, ulong Size, long Modified, ZabbixFileStat Stat);

    private static DirectoryWalk WalkParameters(ZabbixItemKey key, int depthIndex, int excludeDirectoryIndex, int parameters)
    {
        ZabbixItemSet.AtMost(key, parameters);
        string directory = ZabbixItemSet.Parameter(key, 0);
        if (directory.Length == 0) throw ZabbixItemException.InvalidParameter(0);
        Regex? include = ZabbixItemSet.Pattern(key, 1), exclude = ZabbixItemSet.Pattern(key, 2);
        Regex? excludeDirectory = ZabbixItemSet.Pattern(key, excludeDirectoryIndex);
        int depth = ZabbixItemSet.Parameter(key, depthIndex) is { Length: > 0 } text && text != "-1"
            ? int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : throw ZabbixItemException.InvalidParameter(depthIndex)
            : -1;
        // A trailing separator goes, except for what is nothing without it ("/", "C:").
        if (directory.Length > 1 && directory[^2] != ':') directory = directory.TrimEnd('/', '\\');
        if (OperatingSystem.IsWindows())
        {
            if (!Directory.Exists(directory))
            {
                throw new ZabbixItemException(File.Exists(directory)
                    ? "The first parameter names something other than a directory."
                    : $"The directory's details cannot be read: {directory} does not exist.");
            }
        }
        else
        {
            if (!ZabbixUnixCalls.TryStat(directory, followLinks: false, out ZabbixFileStat stat, out int error)) throw Unreadable(error, "directory");
            if (stat.Type != ZabbixFileType.Directory) throw new ZabbixItemException("The first parameter names something other than a directory.");
        }
        return new DirectoryWalk(directory, depth, include, exclude, excludeDirectory);
    }

    private DirectoryFilter FilterParameters(ZabbixItemKey key)
    {
        ZabbixFileType typesIncluded = Types(ZabbixItemSet.Parameter(key, 3)), typesExcluded = Types(ZabbixItemSet.Parameter(key, 4));
        if (typesIncluded == ZabbixFileType.None) typesIncluded = ZabbixFileType.All;
        ulong minSize = Bytes(key, 6, 0, "minimum size"), maxSize = Bytes(key, 7, long.MaxValue, "maximum size");
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        // No minimum age is no upper limit: a file dated ahead of this host's clock still counts.
        long newest = ZabbixItemSet.Parameter(key, 8).Length == 0 ? long.MaxValue : now - (long)Age(key, 8, TimeSpan.Zero, "minimum age").TotalSeconds;
        TimeSpan maxAge = Age(key, 9, TimeSpan.FromSeconds(now), "maximum age");
        return new DirectoryFilter(typesIncluded & ~typesExcluded & ZabbixFileType.All, minSize, maxSize, now - (long)maxAge.TotalSeconds, newest);
    }

    // vfs_dir_info and vfs_dir_size_local's shared walk: last found, first visited; links are not followed; a subdirectory
    // that cannot be read is passed over, the directory asked about is not. regex excl dir sees the path below that
    // directory (sub/deeper), and leaves a matching directory out altogether.
    private static IEnumerable<DirectoryEntry> Walk(DirectoryWalk walk, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((walk.Root, 0));
        while (pending.TryPop(out (string Path, int Depth) next))
        {
            string[] paths;
            try
            {
                paths = Directory.GetFileSystemEntries(next.Path, "*", s_oneDirectory);
            }
            catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
            {
                if (next.Depth > 0) continue;
                throw new ZabbixItemException("The directory cannot be listed.", refused);
            }
            foreach (string path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Entry(path) is not { } entry) continue;
                if (entry.Type == ZabbixFileType.Directory && walk.ExcludeDirectory is { } excluded
                    && excluded.IsMatch(Path.GetRelativePath(walk.Root, path).Replace('\\', '/'))) continue;
                yield return entry;
                if (entry.Type == ZabbixFileType.Directory && (walk.Depth == -1 || next.Depth < walk.Depth)) pending.Push((path, next.Depth + 1));
            }
        }
    }

    private static DirectoryEntry? Entry(string path)
    {
        string name = Path.GetFileName(path);
        if (!OperatingSystem.IsWindows())
        {
            return ZabbixUnixCalls.TryStat(path, followLinks: false, out ZabbixFileStat stat, out _)
                ? new DirectoryEntry(path, name, stat.Type, stat.Size, stat.Modified, stat)
                : null;
        }
        var info = new FileInfo(path);
        FileAttributes attributes = info.Attributes;
        if ((int)attributes == -1) return null;
        bool link = (attributes & FileAttributes.ReparsePoint) != 0, directory = (attributes & FileAttributes.Directory) != 0;
        if (link && directory) return null;
        return new DirectoryEntry(path, name, directory ? ZabbixFileType.Directory : ZabbixFileType.File, directory ? 0 : (ulong)info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(), default);
    }

    private static bool NameMatches(string name, Regex? include, Regex? exclude)
        => (include is null || include.IsMatch(name)) && (exclude is null || !exclude.IsMatch(name));

    private static IEnumerable<DirectoryEntry> Matching(DirectoryWalk walk, DirectoryFilter filter, CancellationToken cancellationToken)
        => Walk(walk, cancellationToken).Where(entry => NameMatches(entry.Name, walk.Include, walk.Exclude)
            && (entry.Type & filter.Types) != 0
            && entry.Size >= filter.MinimumSize && entry.Size <= filter.MaximumSize
            && entry.Modified >= filter.Oldest && entry.Modified <= filter.Newest);

    // vfs_dir_count: dir, regex incl, regex excl, types incl, types excl, max depth, min size, max size, min age, max age,
    // regex excl dir.
    private string DirectoryCount(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        DirectoryFilter filter = FilterParameters(key);
        return ZabbixItemSet.Integer(Matching(WalkParameters(key, 5, 10, 11), filter, cancellationToken).Count());
    }

    // vfs_dir_get: the same parameters; an entry that goes between the walk and the reading of it is left out.
    private string DirectoryGet(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        DirectoryFilter filter = FilterParameters(key);
        DirectoryWalk walk = WalkParameters(key, 5, 10, 11);
        var users = new Dictionary<uint, string>();
        var groups = new Dictionary<uint, string>();
        return ZabbixItemSet.Json(json =>
        {
            json.WriteStartArray();
            foreach (DirectoryEntry entry in Matching(walk, filter, cancellationToken))
            {
                FileDetails details;
                try
                {
                    details = Details(entry.Path, users, groups);
                }
                catch (ZabbixItemException)
                {
                    continue;
                }
                WriteFile(json, details);
            }
            json.WriteEndArray();
        });
    }

    // vfs_dir_size_local: dir, regex incl, regex excl, mode, max depth, regex excl dir. Windows counts what its files take
    // and not its directories' records, and passes over links.
    private static string DirectorySize(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        bool disk = ZabbixItemSet.Mode(key, 3, "apparent", "disk") == "disk";
        DirectoryWalk walk = WalkParameters(key, 4, 5, 6);
        if (OperatingSystem.IsWindows()) return ZabbixItemSet.Integer(WindowsDirectorySize(walk, disk, cancellationToken));
        const ulong BlockSize = 512;    // DISK_BLOCK_SIZE: stat's blocks are 512 bytes, whatever the file system's are
        ulong size = 0;
        if (NameMatches(walk.Root, walk.Include, walk.Exclude))
        {
            if (!ZabbixUnixCalls.TryStat(walk.Root, followLinks: false, out ZabbixFileStat top, out int error)) throw Unreadable(error, "directory");
            size += disk ? top.Blocks * BlockSize : top.Size;
        }
        var seen = new HashSet<(ulong Device, ulong Inode)>();
        foreach (DirectoryEntry entry in Walk(walk, cancellationToken))
        {
            if ((entry.Type & (ZabbixFileType.File | ZabbixFileType.Symlink | ZabbixFileType.Directory)) == 0 || !NameMatches(entry.Name, walk.Include, walk.Exclude)) continue;
            // A file with several names is counted once.
            if (entry.Type == ZabbixFileType.File && entry.Stat.Links > 1 && !seen.Add((entry.Stat.Device, entry.Stat.Inode))) continue;
            size += disk ? entry.Stat.Blocks * BlockSize : entry.Stat.Size;
        }
        return ZabbixItemSet.Integer(size);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static ulong WindowsDirectorySize(DirectoryWalk walk, bool disk, CancellationToken cancellationToken)
    {
        ulong size = 0;
        var seen = new HashSet<(uint Volume, ulong Index)>();
        var clusters = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (DirectoryEntry entry in Walk(walk, cancellationToken))
        {
            if (entry.Type != ZabbixFileType.File) continue;
            if ((new FileInfo(entry.Path).Attributes & FileAttributes.ReparsePoint) != 0) continue;
            // link_processed: a file that cannot be opened is passed over, and one with several names is counted once.
            if (ZabbixWindowsCalls.Identity(entry.Path) is not { } identity) continue;
            if (identity.Links > 1 && !seen.Add((identity.Volume, identity.Index))) continue;
            if (!NameMatches(entry.Name, walk.Include, walk.Exclude) || ZabbixWindowsCalls.CompressedSize(entry.Path) is not { } taken) continue;
            if (disk)
            {
                string directory = Path.GetDirectoryName(entry.Path) ?? walk.Root;
                if (!clusters.TryGetValue(directory, out ulong cluster)) clusters[directory] = cluster = ZabbixWindowsCalls.ClusterSize(directory);
                if (taken % cluster != 0) taken += cluster - (taken % cluster);
            }
            size += taken;
        }
        return size;
    }

    // A size with Zabbix's memory suffixes: K, M, G, T.
    private static ulong Bytes(ZabbixItemKey key, int index, ulong absent, string what)
    {
        string text = ZabbixItemSet.Parameter(key, index);
        if (text.Length == 0) return absent;
        ulong multiplier = char.ToUpperInvariant(text[^1]) switch { 'K' => 1UL << 10, 'M' => 1UL << 20, 'G' => 1UL << 30, 'T' => 1UL << 40, _ => 1 };
        string digits = multiplier == 1 ? text : text[..^1];
        return ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) ? value * multiplier : throw new ZabbixItemException($"\"{text}\" is not a valid {what}.");
    }

    // An age with Zabbix's time suffixes: s, m, h, d, w.
    private static TimeSpan Age(ZabbixItemKey key, int index, TimeSpan absent, string what)
    {
        string text = ZabbixItemSet.Parameter(key, index);
        return text.Length == 0 ? absent : ZabbixItemSet.Duration(text) ?? throw new ZabbixItemException($"\"{text}\" is not a valid {what}.");
    }

    private static FileInfo Info(string name)
    {
        var info = new FileInfo(name);
        return info.Exists ? info : throw new ZabbixItemException($"The file's details cannot be read: {name} does not exist.");
    }

    private static FileStream Open(string name)
    {
        try
        {
            return new FileStream(name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, useAsync: true);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            throw new ZabbixItemException($"The file cannot be opened: {refused.Message}", refused);
        }
    }

    private static ZabbixItemException TooLarge() => new("The file is larger than this key reads.");

    // Counted as it is read: a device or a /proc file reports no length, and may have no end.
    private static async ValueTask<byte[]> ReadAsync(string name, CancellationToken cancellationToken)
    {
        FileStream file = Open(name);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable closing = file.ConfigureAwait(false);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[65536];
        int read;
        while ((read = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > MaxContents) throw TooLarge();
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return memory.ToArray();
    }

    // The file's lines as they are read, so a search of a large log holds one line and stops at its match. A line ends at
    // a newline and a carriage return before it goes; what follows a last newline is not a line, and an empty file has none.
    private static async IAsyncEnumerable<string> LinesAsync(string name, Encoding? encoding, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        FileStream file = Open(name);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable closing = file.ConfigureAwait(false);
        // A named encoding's own byte order mark is left out; with none named, the mark says which it is.
        using var reader = new StreamReader(file, encoding ?? System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: encoding is null, bufferSize: 65536, leaveOpen: true);
        char[] buffer = new char[65536];
        var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            int start = 0;
            while (start < read)
            {
                int newline = Array.IndexOf(buffer, '\n', start, read - start);
                int end = newline < 0 ? read : newline;
                if (line.Length + (end - start) > MaxContents) throw TooLarge();
                line.Append(buffer, start, end - start);
                start = end + 1;
                if (newline < 0) break;
                yield return line.ToString().TrimEnd('\r');
                line.Clear();
            }
        }
        if (line.Length > 0) yield return line.ToString().TrimEnd('\r');
    }

    // The encoding a key names (a code page identifier, as iconv names it), or null for UTF-8 with its BOM detected.
    private static Encoding? Encoding(string name, int index)
    {
        if (name.Length == 0) return null;
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(name) ?? System.Text.Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            throw ZabbixItemException.InvalidParameter(index);
        }
    }

    // The text without its byte order mark, which Zabbix's agent leaves out.
    private static string Decode(byte[] bytes, Encoding? encoding)
    {
        ReadOnlySpan<byte> data = bytes;
        Encoding used = encoding ?? System.Text.Encoding.UTF8;
        int skip = 0;
        if (encoding is null)
        {
            if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) skip = 3;
            else if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) (used, skip) = (System.Text.Encoding.Unicode, 2);
            else if (data.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) (used, skip) = (System.Text.Encoding.BigEndianUnicode, 2);
        }
        else if (!encoding.Preamble.IsEmpty && data.StartsWith(encoding.Preamble))
        {
            skip = encoding.Preamble.Length;
        }
        return used.GetString(data[skip..]);
    }
}
