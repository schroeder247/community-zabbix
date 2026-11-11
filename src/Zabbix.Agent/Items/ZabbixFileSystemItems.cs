// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;

namespace Community.Zabbix.Items;

/// <summary>
/// Provides Zabbix's file system and block device keys: the mounted file systems, their space and inodes
/// (<c>vfs.fs.discovery</c>, <c>vfs.fs.get</c>, <c>vfs.fs.size</c>, <c>vfs.fs.inode</c>), and on Linux the block devices and their
/// counters (<c>vfs.dev.discovery</c>, <c>vfs.dev.read</c>, <c>vfs.dev.write</c>).
/// </summary>
/// <remarks>
/// A file system's figures are Zabbix's: <c>total</c> is its size, <c>free</c> the space a user may still write (the
/// reserved blocks excluded), <c>used</c> what is written, and the percentages are of <c>used</c> + <c>free</c>. They
/// come from <see cref="DriveInfo"/>, which reports the three block counts Zabbix's agent reads. The inode figures are
/// <c>statvfs</c>'s on Linux and macOS; Windows has none. A block device's per-second types (<c>sps</c>, <c>ops</c>) sample once a second from their
/// first request, which waits for two samples within the check's deadline; a provider built on its own stops sampling when
/// it is disposed.
/// </remarks>
public sealed class ZabbixFileSystemItems : ZabbixItemProvider, IDisposable
{
    private readonly ZabbixItemSet _set = new();
    private readonly ZabbixItemCollector _collector;
    private readonly ZabbixItemCollector? _owned;
    private readonly ZabbixHostFiles _files;

    /// <summary>The file system keys of the host this process runs on.</summary>
    /// <param name="time">The clock the block devices' per-second types sample on; the system clock by default.</param>
    public ZabbixFileSystemItems(TimeProvider? time = null)
        : this(ZabbixHostFiles.Host, time ?? TimeProvider.System)
    {
    }

    internal ZabbixFileSystemItems(ZabbixHostFiles files, TimeProvider time)
        : this(new ZabbixItemCollector(files, time), owned: true)
    {
    }

    internal ZabbixFileSystemItems(ZabbixItemCollector collector, bool owned = false)
    {
        _collector = collector;
        _owned = owned ? collector : null;
        _files = collector.Files;
        _set.Add("vfs.fs.discovery", "The mounted file systems, for low-level discovery", _ => Discovery())
            .Add("vfs.fs.get", "The mounted file systems and their space, as JSON", _ => Get())
            .Add("vfs.fs.size", "A file system's space, in bytes or as a percentage", Size, "vfs.fs.size[/,free]")
            .Add("vfs.fs.inode", "A file system's inodes, as a count or a percentage", Inode, "vfs.fs.inode[/,pfree]")
            .Add("vfs.dev.discovery", "The block devices, for low-level discovery", _ => DeviceDiscovery())
            .Add("vfs.dev.read", "A block device's reads", (key, token) => DeviceCounterAsync(key, read: true, token), "vfs.dev.read[sda,operations]")
            .Add("vfs.dev.write", "A block device's writes", (key, token) => DeviceCounterAsync(key, read: false, token), "vfs.dev.write[sda,operations]");
    }

    /// <inheritdoc/>
    public override IReadOnlyList<ZabbixItemDefinition> Items => _set.Items;

    /// <inheritdoc/>
    public override ValueTask<ZabbixAgentAnswer> GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken) => _set.AnswerAsync(key, cancellationToken);

    /// <summary>Stops the block devices' sampling.</summary>
    public void Dispose() => _owned?.Dispose();

    private sealed record Mount(string Name, string Type, string? Options, DriveInfo Drive);

    // /proc/mounts on Linux (as Zabbix's agent reads it), the drives .NET lists elsewhere.
    private List<Mount> Mounts()
    {
        if (!_files.IsLinux) return [.. DriveInfo.GetDrives().Select(drive => new Mount(MountName(drive), SafeFormat(drive), null, drive))];
        var mounts = new List<Mount>();
        foreach (string line in _files.ReadLines("/proc/mounts"))
        {
            string[] fields = line.Split(' ');
            if (fields.Length < 4) continue;
            string point = Unescape(fields[1]);
            mounts.Add(new Mount(point, fields[2], fields[3], new DriveInfo(point)));
        }
        return mounts;

        // /proc/mounts writes a space as \040, a tab as \011, a newline as \012 and a backslash as \134.
        static string Unescape(string field) => field.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);
    }

    // Windows names a drive "C:" (its root without the separator), as Zabbix's agent does.
    private static string MountName(DriveInfo drive) => OperatingSystem.IsWindows() ? drive.Name.TrimEnd('\\') : drive.Name;

    private static string SafeFormat(DriveInfo drive)
    {
        try
        {
            return drive.DriveFormat;
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private string Discovery() => ZabbixItemSet.Json(json =>
    {
        json.WriteStartArray();
        foreach (Mount mount in Mounts())
        {
            json.WriteStartObject();
            json.WriteString("{#FSNAME}", mount.Name);
            json.WriteString("{#FSTYPE}", mount.Type);
            if (OperatingSystem.IsWindows())
            {
                json.WriteString("{#FSLABEL}", Label(mount.Drive));
                json.WriteString("{#FSDRIVETYPE}", DriveType(mount.Drive));
            }
            if (mount.Options is not null) json.WriteString("{#FSOPTIONS}", mount.Options);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    });

    private string Get() => ZabbixItemSet.Json(json =>
    {
        json.WriteStartArray();
        foreach (Mount mount in Mounts())
        {
            // A file system whose space cannot be read is left out, as Zabbix's agent leaves it out.
            if (Space(mount.Drive) is not { } space) continue;
            json.WriteStartObject();
            json.WriteString("fsname", mount.Name);
            json.WriteString("fstype", mount.Type);
            if (OperatingSystem.IsWindows())
            {
                json.WriteString("fslabel", Label(mount.Drive));
                json.WriteString("fsdrivetype", DriveType(mount.Drive));
            }
            json.WriteStartObject("bytes");
            json.WriteNumber("total", space.Total);
            json.WriteNumber("free", space.Free);
            json.WriteNumber("used", space.Used);
            ZabbixItemSet.WriteFloat(json, "pfree", space.PercentFree);
            ZabbixItemSet.WriteFloat(json, "pused", space.PercentUsed);
            json.WriteEndObject();
            if (MountInodes(mount.Name) is { } inodes)
            {
                json.WriteStartObject("inodes");
                json.WriteNumber("total", inodes.Total);
                json.WriteNumber("free", inodes.Free);
                json.WriteNumber("used", inodes.Used);
                ZabbixItemSet.WriteFloat(json, "pfree", inodes.PercentFree);
                ZabbixItemSet.WriteFloat(json, "pused", inodes.PercentUsed);
                json.WriteEndObject();
            }
            if (mount.Options is not null) json.WriteString("options", mount.Options);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    });

    private static string Size(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 2);
        string name = ZabbixItemSet.Parameter(key, 0);
        if (name.Length == 0) throw ZabbixItemException.InvalidParameter(0);
        string mode = ZabbixItemSet.Mode(key, 1, "total", "free", "used", "pfree", "pused");
        DriveInfo drive;
        try
        {
            drive = new DriveInfo(name);
        }
        catch (ArgumentException invalid)
        {
            throw new ZabbixItemException($"The file system's details cannot be read: {invalid.Message}", invalid);
        }
        FileSystemSpace space = Space(drive) ?? throw new ZabbixItemException($"The file system's details cannot be read: the file system at {name} cannot be read.");
        return mode switch
        {
            "total" => ZabbixItemSet.Integer(space.Total),
            "free" => ZabbixItemSet.Integer(space.Free),
            "used" => ZabbixItemSet.Integer(space.Used),
            "pfree" => ZabbixItemSet.Float(space.PercentFree),
            _ => ZabbixItemSet.Float(space.PercentUsed),
        };
    }

    // vfs_fs_inode (linux/inodes.c, osx/inodes.c). NTFS has no inode count to report.
    private static string Inode(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 2);
        string name = ZabbixItemSet.Parameter(key, 0);
        if (name.Length == 0) throw ZabbixItemException.InvalidParameter(0);
        string mode = ZabbixItemSet.Mode(key, 1, "total", "free", "used", "pfree", "pused");
        if (OperatingSystem.IsWindows()) throw ZabbixItemException.NotOnThisSystem();
        if (!ZabbixUnixCalls.TryInodes(name, out ZabbixInodes inodes, out int error))
            throw new ZabbixItemException($"The file system's details cannot be read: {System.Runtime.InteropServices.Marshal.GetPInvokeErrorMessage(error)}");
        return mode switch
        {
            "total" => ZabbixItemSet.Integer(inodes.Total),
            "free" => ZabbixItemSet.Integer(inodes.Free),
            "used" => ZabbixItemSet.Integer(inodes.Used),
            "pfree" => ZabbixItemSet.Float(inodes.PercentFree),
            _ => ZabbixItemSet.Float(inodes.PercentUsed),
        };
    }

    private static ZabbixInodes? MountInodes(string name)
    {
        if (OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && IntPtr.Size != sizeof(long))) return null;
        return ZabbixUnixCalls.TryInodes(name, out ZabbixInodes inodes, out _) ? inodes : null;
    }

    private readonly record struct FileSystemSpace(ulong Total, ulong Free, ulong Used, double PercentFree, double PercentUsed);

    // get_fs_size_stat (linux/diskspace.c): free is the caller's available space; the percentages are of used + free.
    private static FileSystemSpace? Space(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady) return null;
            ulong total = (ulong)drive.TotalSize, available = (ulong)drive.AvailableFreeSpace, unused = (ulong)drive.TotalFreeSpace;
            ulong used = total - unused;
            double pfree = used + available == 0 ? 0 : 100.0 * available / (used + available);
            return new FileSystemSpace(total, available, used, pfree, used + available == 0 ? 0 : 100.0 - pfree);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Label(DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel;
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return string.Empty;
        }
    }

    // {#FSDRIVETYPE}: GetDriveType's names, as Zabbix's Windows agent writes them.
    private static string DriveType(DriveInfo drive) => drive.DriveType switch
    {
        System.IO.DriveType.Fixed => "fixed",
        System.IO.DriveType.Removable => "removable",
        System.IO.DriveType.Network => "network",
        System.IO.DriveType.CDRom => "cdrom",
        System.IO.DriveType.Ram => "ramdisk",
        System.IO.DriveType.NoRootDirectory => "norootdir",
        _ => "unknown",
    };

    // vfs_dev_discovery (linux/diskio.c): every block device, with the DEVTYPE its uevent names.
    private string DeviceDiscovery()
    {
        if (!_files.IsLinux) throw ZabbixItemException.NotOnThisSystem();
        if (!_files.DirectoryExists("/sys/class/block")) throw new ZabbixItemException("Cannot obtain device list: failed to open /dev/ directory.");
        return ZabbixItemSet.Json(json =>
        {
            json.WriteStartArray();
            foreach (string device in _files.Directories("/sys/class/block").Concat(BlockLinks()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                string? type = null;
                foreach (string line in _files.TryReadAllText($"/sys/class/block/{device}/uevent")?.Split('\n') ?? [])
                {
                    if (line.StartsWith("DEVTYPE=", StringComparison.Ordinal)) type = line["DEVTYPE=".Length..];
                }
                json.WriteStartObject();
                json.WriteString("{#DEVNAME}", device);
                json.WriteString("{#DEVTYPE}", type ?? string.Empty);
                json.WriteEndObject();
            }
            json.WriteEndArray();
        });
    }

    // /sys/class/block holds links, which Directory.EnumerateDirectories lists only when it follows them.
    private IEnumerable<string> BlockLinks()
    {
        string resolved = _files.Resolve("/sys/class/block");
        return Directory.Exists(resolved) ? Directory.EnumerateFileSystemEntries(resolved).Select(entry => Path.GetFileName(entry)) : [];
    }

    // vfs_dev_rw (linux/diskio.c): sectors and operations from /proc/diskstats; sps and ops from the collector.
    private async ValueTask<string> DeviceCounterAsync(ZabbixItemKey key, bool read, CancellationToken cancellationToken)
    {
        ZabbixItemSet.AtMost(key, 3);
        string device = ZabbixItemSet.Parameter(key, 0);
        string type = ZabbixItemSet.Mode(key, 1, "sps", "ops", "sectors", "operations");
        if (!_files.IsLinux) throw ZabbixItemException.NotOnThisSystem();
        bool all = device.Length == 0 || device == "all";
        string name = device.StartsWith("/dev/", StringComparison.Ordinal) ? device[5..] : device;
        if (!all && ZabbixHostFiles.LinkTarget(_files.Resolve("/dev/" + name)) is { } target) name = Path.GetFileName(target);
        if (type is "sps" or "ops")
        {
            int seconds = ZabbixItemSet.Window(key, 2);
            double rate = await _collector.Devices.RateAsync(all ? ZabbixDeviceSamples.All : name, read, sectors: type == "sps", seconds, cancellationToken).ConfigureAwait(false);
            return ZabbixItemSet.Float(rate);
        }
        if (key.Parameters.Count > 2) throw new ZabbixItemException("The key has the wrong number of parameters for this type.");
        ulong sectors = 0, operations = 0;
        bool found = false;
        foreach (string line in _files.ReadLines("/proc/diskstats"))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10 || (!all && fields[2] != name)) continue;
            operations += ulong.Parse(fields[read ? 3 : 7], NumberStyles.None, CultureInfo.InvariantCulture);
            sectors += ulong.Parse(fields[read ? 5 : 9], NumberStyles.None, CultureInfo.InvariantCulture);
            found = true;
        }
        if (!found) throw new ZabbixItemException("The block device's counters cannot be read.");
        return ZabbixItemSet.Integer(type == "sectors" ? sectors : operations);
    }
}
