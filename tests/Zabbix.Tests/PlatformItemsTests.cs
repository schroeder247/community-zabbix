// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Community.Zabbix.Items;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The bundled keys that make platform calls (docs/adr/0009-bundled-items.md §6). These run the native calls for real, so
/// each runs on the systems it names: <c>statx</c> on Linux, <c>lstat</c> and the Mach calls on macOS, the Win32 calls on
/// Windows.
/// </summary>
public sealed class PlatformItemsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"zbx-platform-{Guid.NewGuid():N}");
    private readonly ZabbixFileItems _files = new();

    public PlatformItemsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A later run's temp cleaning takes what this one could not.
        }
    }

    private static void SkipOnWindows() => Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no stat.");

    private static async Task<string> ValueAsync(ZabbixItemProvider items, string key)
    {
        ZabbixAgentAnswer answer = await items.GetValueAsync(ZabbixItemKey.Parse(key, null), Ct);
        Assert.True(answer.IsSupported, $"{key}: {answer.Error}");
        return answer.Value!;
    }

    private static async Task<string?> ErrorAsync(ZabbixItemProvider items, string key)
    {
        ZabbixAgentAnswer answer = await items.GetValueAsync(ZabbixItemKey.Parse(key, null), Ct);
        Assert.False(answer.IsSupported, $"{key} answered {answer.Value}");
        return answer.Error;
    }

    private string Write(string relative, string content = "hello\n")
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static void Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task A_line_search_sees_only_the_lines_a_file_has()
    {
        string ended = Write("ended.txt", "a\nb\n"), empty = Write("empty.txt", string.Empty), blank = Write("blank.txt", "a\n\nb"), crlf = Write("crlf.txt", "a\r\nb");

        Assert.Equal("0", await ValueAsync(_files, $"vfs.file.regmatch[{ended},^$]"));
        Assert.Equal(string.Empty, await ValueAsync(_files, $"vfs.file.regexp[{ended},^$]"));
        Assert.Equal("0", await ValueAsync(_files, $"vfs.file.regmatch[{empty},^$]"));
        Assert.Equal("1", await ValueAsync(_files, $"vfs.file.regmatch[{blank},^$]"));
        Assert.Equal("1", await ValueAsync(_files, $"vfs.file.regmatch[{crlf},^a$]"));
        Assert.Equal("b", await ValueAsync(_files, $"vfs.file.regexp[{crlf},^b$]"));
    }

    [Fact]
    public async Task Reading_what_has_no_end_stops_at_the_limit()
    {
        SkipOnWindows();
        Assert.Equal("The file is larger than this key reads.", await ErrorAsync(_files, "vfs.file.contents[/dev/zero]"));
        Assert.Equal("The file is larger than this key reads.", await ErrorAsync(_files, "vfs.file.regmatch[/dev/zero,^x$]"));
    }

    [Fact]
    public async Task A_file_dated_ahead_of_the_clock_counts_unless_a_minimum_age_is_asked_for()
    {
        var early = new ZabbixFileItems(new FakeTimeProvider(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        string directory = Path.GetDirectoryName(Write("ahead/file.txt"))!;

        Assert.Equal("1", await ValueAsync(early, $"vfs.dir.count[{directory}]"));
        Assert.Equal("0", await ValueAsync(early, $"vfs.dir.count[{directory},,,,,,,,1s]"));
    }

    private static ulong Number(string value) => ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static double Percent(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    [Fact]
    public async Task Exists_tells_every_type_of_file_apart()
    {
        SkipOnWindows();
        string file = Write("file");
        string link = Path.Combine(_root, "link"), broken = Path.Combine(_root, "broken"), fifo = Path.Combine(_root, "fifo");
        File.CreateSymbolicLink(link, "file");
        File.CreateSymbolicLink(broken, "/nonexistent/target");
        Run("mkfifo", fifo);

        Assert.Equal("1", await ValueAsync(_files, $"vfs.file.exists[{file}]"));
        Assert.Equal("1", await ValueAsync(_files, $"vfs.file.exists[{_root},dir]"));
        Assert.Equal("0", await ValueAsync(_files, $"vfs.file.exists[{_root}]"));
        Assert.Equal("1", await ValueAsync(_files, $"vfs.file.exists[{link}]"));                  // a link is what it points at
        Assert.Equal("1", await ValueAsync(_files, $"vfs.file.exists[{link},sym]"));              // and is itself
        Assert.Equal("0", await ValueAsync(_files, $"vfs.file.exists[{link},,sym]"));             // so excluding links excludes it
        Assert.Equal("0", await ValueAsync(_files, $"vfs.file.exists[{broken}]"));
        Assert.Equal("1", await ValueAsync(_files, $"vfs.file.exists[{broken},sym]"));
        Assert.Equal("0", await ValueAsync(_files, $"vfs.file.exists[{fifo}]"));
        Assert.Equal("1", await ValueAsync(_files, $"vfs.file.exists[{fifo},fifo]"));
        Assert.Equal("1", await ValueAsync(_files, $"vfs.file.exists[{fifo},all]"));
        Assert.Equal("1", await ValueAsync(_files, "vfs.file.exists[/dev/null,cdev]"));
        Assert.Equal("1", await ValueAsync(_files, "vfs.file.exists[/dev/null,dev]"));
        Assert.Equal("0", await ValueAsync(_files, "vfs.file.exists[/dev/null,bdev]"));
        Assert.Equal("1", await ValueAsync(_files, $"vfs.file.exists[{file},,dir]"));             // anything but a directory
        Assert.Equal("0", await ValueAsync(_files, $"vfs.file.exists[{file},file,file]"));
        Assert.Equal("0", await ValueAsync(_files, $"vfs.file.exists[{file}.missing,all]"));
        Assert.Equal("\"nope\" is not a file type.", await ErrorAsync(_files, $"vfs.file.exists[{file},nope]"));
    }

    [Fact]
    public async Task Exists_sees_a_socket()
    {
        SkipOnWindows();
        // The path a socket binds to is short everywhere, and a temp directory's may not be.
        string path = $"/tmp/zbx-{Guid.NewGuid():N}"[..20];
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        try
        {
            Assert.Equal("1", await ValueAsync(_files, $"vfs.file.exists[{path},sock]"));
            Assert.Equal("0", await ValueAsync(_files, $"vfs.file.exists[{path}]"));
            Assert.Contains("\"type\":\"sock\"", await ValueAsync(_files, $"vfs.file.get[{path}]"), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_change_time_is_answered_with_the_others()
    {
        string file = Write("file");
        long modified = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds();
        Assert.Equal(modified.ToString(CultureInfo.InvariantCulture), await ValueAsync(_files, $"vfs.file.time[{file}]"));
        long changed = (long)Number(await ValueAsync(_files, $"vfs.file.time[{file},change]"));
        Assert.InRange(changed, modified - 5, modified + 5);
        Assert.Equal("The key's second parameter is not valid.", await ErrorAsync(_files, $"vfs.file.time[{file},birth]"));
        Assert.StartsWith("The file's details cannot be read:", await ErrorAsync(_files, $"vfs.file.time[{file}.missing,change]"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_owner_is_a_name_or_an_id_on_Unix()
    {
        SkipOnWindows();
        string file = Write("file");
        Assert.Equal(Environment.UserName, await ValueAsync(_files, $"vfs.file.owner[{file}]"));
        Assert.Equal(Environment.UserName, await ValueAsync(_files, $"vfs.file.owner[{file},user,name]"));
        Assert.NotEmpty(await ValueAsync(_files, $"vfs.file.owner[{file},group]"));
        _ = Number(await ValueAsync(_files, $"vfs.file.owner[{file},user,id]"));
        _ = Number(await ValueAsync(_files, $"vfs.file.owner[{file},group,id]"));
        Assert.Equal("root", await ValueAsync(_files, "vfs.file.owner[/]"));
        Assert.Equal("0", await ValueAsync(_files, "vfs.file.owner[/,user,id]"));
        Assert.Equal("The key's second parameter is not valid.", await ErrorAsync(_files, $"vfs.file.owner[{file},nobody]"));
        Assert.Equal("The key's third parameter is not valid.", await ErrorAsync(_files, $"vfs.file.owner[{file},user,sid]"));
        Assert.StartsWith("The file's details cannot be read:", await ErrorAsync(_files, $"vfs.file.owner[{file}.missing]"), StringComparison.Ordinal);
        Assert.Equal("The key's first parameter is not valid.", await ErrorAsync(_files, "vfs.file.owner[]"));
        Assert.Equal("The key has more parameters than it takes.", await ErrorAsync(_files, $"vfs.file.owner[{file},user,id,extra]"));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task The_owner_is_an_account_or_a_SID_on_Windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows' owner is a security descriptor's.");
        string file = Write("file");
        // A new file's owner is its creator's token's: the user, or the Administrators group for an elevated user on Windows Server.
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().Owner!;
        Assert.Equal(owner.Translate(typeof(NTAccount)).Value, await ValueAsync(_files, $"vfs.file.owner[{file}]"), ignoreCase: true);
        Assert.Equal(owner.Value, await ValueAsync(_files, $"vfs.file.owner[{file},user,id]"));
        Assert.Equal("The key's second parameter is not valid.", await ErrorAsync(_files, $"vfs.file.owner[{file},group]"));
        Assert.Equal("The key's third parameter is not valid.", await ErrorAsync(_files, $"vfs.file.owner[{file},user,sid]"));
    }

    [Fact]
    public async Task A_file_is_described_as_Zabbix_describes_it()
    {
        string file = Write(Path.Combine("sub", "file.txt"), "12345");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        using JsonDocument document = JsonDocument.Parse(await ValueAsync(_files, $"vfs.file.get[{file}]"));
        JsonElement json = document.RootElement;
        string[] names = [.. json.EnumerateObject().Select(member => member.Name)];
        Assert.Equal(
            OperatingSystem.IsWindows()
                ? ["basename", "pathname", "dirname", "type", "user", "SID", "size", "time", "timestamp"]
                : ["basename", "pathname", "dirname", "type", "user", "group", "permissions", "uid", "gid", "size", "time", "timestamp"],
            names);
        Assert.Equal("file.txt", json.GetProperty("basename").GetString());
        Assert.Equal(Path.GetFullPath(file), json.GetProperty("pathname").GetString());
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(file)), json.GetProperty("dirname").GetString());
        Assert.Equal("file", json.GetProperty("type").GetString());
        Assert.Equal(5, json.GetProperty("size").GetInt64());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(Environment.UserName, json.GetProperty("user").GetString());
            Assert.Equal("0640", json.GetProperty("permissions").GetString());
        }
        long modified = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds();
        Assert.Equal(modified, json.GetProperty("timestamp").GetProperty("modify").GetInt64());
        Assert.Equal(["access", "modify", "change"], json.GetProperty("time").EnumerateObject().Select(member => member.Name));
        Assert.StartsWith("The file's details cannot be read:", await ErrorAsync(_files, $"vfs.file.get[{file}.missing]"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_files_times_are_written_in_the_hosts_time_zone()
    {
        string file = Write("file");
        File.SetLastWriteTimeUtc(file, new DateTime(2026, 7, 1, 12, 30, 45, DateTimeKind.Utc));
        var time = new FakeTimeProvider();
        time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("zbx", TimeSpan.FromHours(-5.5), "zbx", "zbx"));
        using JsonDocument document = JsonDocument.Parse(await ValueAsync(new ZabbixFileItems(time), $"vfs.file.get[{file}]"));
        Assert.Equal("2026-07-01T07:00:45-0530", document.RootElement.GetProperty("time").GetProperty("modify").GetString());
    }

    [Fact]
    public async Task The_root_and_a_link_are_described_as_themselves()
    {
        SkipOnWindows();
        using (JsonDocument root = JsonDocument.Parse(await ValueAsync(_files, "vfs.file.get[/]")))
        {
            Assert.Equal("/", root.RootElement.GetProperty("basename").GetString());
            Assert.Equal("/", root.RootElement.GetProperty("pathname").GetString());
            Assert.Equal("/", root.RootElement.GetProperty("dirname").GetString());
            Assert.Equal("dir", root.RootElement.GetProperty("type").GetString());
        }
        string link = Path.Combine(_root, "link");
        File.CreateSymbolicLink(link, "/nonexistent/target");
        using JsonDocument described = JsonDocument.Parse(await ValueAsync(_files, $"vfs.file.get[{link}]"));
        Assert.Equal("sym", described.RootElement.GetProperty("type").GetString());
        Assert.Equal("/nonexistent/target".Length, described.RootElement.GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task A_directory_lists_what_matches()
    {
        Write("a.log");
        Write(Path.Combine("sub", "b.log"));
        Write(Path.Combine("sub", "c.txt"));
        Write(Path.Combine("skipped", "d.log"));

        using JsonDocument everything = JsonDocument.Parse(await ValueAsync(_files, $"vfs.dir.get[{_root}]"));
        string[] names = [.. everything.RootElement.EnumerateArray().Select(entry => entry.GetProperty("basename").GetString()!).Order(StringComparer.Ordinal)];
        Assert.Equal(["a.log", "b.log", "c.txt", "d.log", "skipped", "sub"], names);

        using JsonDocument logs = JsonDocument.Parse(await ValueAsync(_files, $@"vfs.dir.get[{_root},""\.log$"",,file,,,,,,,""^skipped$""]"));
        Assert.Equal(["a.log", "b.log"], logs.RootElement.EnumerateArray().Select(entry => entry.GetProperty("basename").GetString()!).Order(StringComparer.Ordinal));
        JsonElement first = logs.RootElement[0];
        Assert.Equal("basename", first.EnumerateObject().First().Name);
        Assert.Equal("file", first.GetProperty("type").GetString());

        using JsonDocument top = JsonDocument.Parse(await ValueAsync(_files, $"vfs.dir.get[{_root},,,,,0]"));
        Assert.Equal(["a.log", "skipped", "sub"], top.RootElement.EnumerateArray().Select(entry => entry.GetProperty("basename").GetString()!).Order(StringComparer.Ordinal));

        Assert.Equal("[]", await ValueAsync(_files, $"vfs.dir.get[{_root},nothing-is-called-this]"));
        Assert.Equal("The key's sixth parameter is not valid.", await ErrorAsync(_files, $"vfs.dir.get[{_root},,,,,x]"));
        Assert.Equal("\"nope\" is not a file type.", await ErrorAsync(_files, $"vfs.dir.get[{_root},,,nope]"));
        Assert.Equal("\"x\" is not a valid minimum size.", await ErrorAsync(_files, $"vfs.dir.get[{_root},,,,,,x]"));
        Assert.Equal("\"x\" is not a valid maximum age.", await ErrorAsync(_files, $"vfs.dir.get[{_root},,,,,,,,,x]"));
        Assert.StartsWith("The second parameter is not a valid regular expression:", await ErrorAsync(_files, $@"vfs.dir.get[{_root},""(""]"), StringComparison.Ordinal);
        Assert.StartsWith("The eleventh parameter is not a valid regular expression:", await ErrorAsync(_files, $@"vfs.dir.get[{_root},,,,,,,,,,""(""]"), StringComparison.Ordinal);
        Assert.Equal("The key has more parameters than it takes.", await ErrorAsync(_files, $"vfs.dir.get[{_root},,,,,,,,,,,extra]"));
    }

    [Fact]
    public async Task A_directory_that_is_not_one_is_refused()
    {
        string file = Write("file");
        Assert.Equal("The first parameter names something other than a directory.", await ErrorAsync(_files, $"vfs.dir.count[{file}]"));
        Assert.Equal("The first parameter names something other than a directory.", await ErrorAsync(_files, $"vfs.dir.size[{file}]"));
        Assert.Equal("The first parameter names something other than a directory.", await ErrorAsync(_files, $"vfs.dir.get[{file}]"));
        Assert.StartsWith("The directory's details cannot be read:", await ErrorAsync(_files, $"vfs.dir.get[{file}.missing]"), StringComparison.Ordinal);
        Assert.Equal("The key's first parameter is not valid.", await ErrorAsync(_files, "vfs.dir.get[]"));
    }

    [Fact]
    public async Task Links_are_not_followed_and_a_special_file_has_its_own_type()
    {
        SkipOnWindows();
        Write(Path.Combine("real", "inside"));
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Write(Path.Combine("outside", "other"));
        string tree = Path.Combine(_root, "real");
        File.CreateSymbolicLink(Path.Combine(tree, "link"), outside);
        Run("mkfifo", Path.Combine(tree, "fifo"));

        Assert.Equal("3", await ValueAsync(_files, $"vfs.dir.count[{tree}]"));
        Assert.Equal("1", await ValueAsync(_files, $"vfs.dir.count[{tree},,,file]"));
        Assert.Equal("1", await ValueAsync(_files, $"vfs.dir.count[{tree},,,sym]"));
        Assert.Equal("1", await ValueAsync(_files, $"vfs.dir.count[{tree},,,fifo]"));
        Assert.Equal("2", await ValueAsync(_files, $"vfs.dir.count[{tree},,,,fifo]"));
        Assert.Equal("The first parameter names something other than a directory.", await ErrorAsync(_files, $"vfs.dir.count[{Path.Combine(tree, "link")}]"));
    }

    [Fact]
    public async Task A_directory_excluded_by_its_path_is_not_counted_either()
    {
        Write(Path.Combine("keep", "a"));
        Write(Path.Combine("drop", "b"));
        Assert.Equal("4", await ValueAsync(_files, $"vfs.dir.count[{_root}]"));
        Assert.Equal("2", await ValueAsync(_files, $@"vfs.dir.count[{_root},,,,,,,,,,""^drop$""]"));
    }

    [Fact]
    public async Task A_directorys_size_counts_a_file_with_two_names_once()
    {
        SkipOnWindows();
        string tree = Path.Combine(_root, "tree");
        Directory.CreateDirectory(tree);
        Write(Path.Combine("tree", "one"), "12345");
        Run("ln", Path.Combine(tree, "one"), Path.Combine(tree, "two"));
        Write(Path.Combine("tree", "sub", "three"), "1234567");
        File.CreateSymbolicLink(Path.Combine(tree, "link"), "one");
        ulong directories = (ulong)(StatSize(tree) + StatSize(Path.Combine(tree, "sub")));

        Assert.Equal(directories + 5 + 7 + 3, Number(await ValueAsync(_files, $"vfs.dir.size[{tree}]")));
        Assert.Equal((ulong)StatSize(tree) + 5 + (ulong)StatSize(Path.Combine(tree, "sub")) + 3, Number(await ValueAsync(_files, $"vfs.dir.size[{tree},,,,0]")));      // the top level only
        Assert.Equal(7UL, Number(await ValueAsync(_files, $@"vfs.dir.size[{tree},""^three$""]")));
        Assert.Equal((ulong)StatSize(tree) + 5 + 3, Number(await ValueAsync(_files, $@"vfs.dir.size[{tree},,,,,""^sub$""]")));
        Assert.True(Number(await ValueAsync(_files, $"vfs.dir.size[{tree},,,disk]")) > 0);
        Assert.Equal("The key's fourth parameter is not valid.", await ErrorAsync(_files, $"vfs.dir.size[{tree},,,sideways]"));
        Assert.Equal("The key's fifth parameter is not valid.", await ErrorAsync(_files, $"vfs.dir.size[{tree},,,,x]"));
        Assert.StartsWith("The sixth parameter is not a valid regular expression:", await ErrorAsync(_files, $@"vfs.dir.size[{tree},,,,,""(""]"), StringComparison.Ordinal);
        Assert.Equal("The key has more parameters than it takes.", await ErrorAsync(_files, $"vfs.dir.size[{tree},,,,,,extra]"));
    }

    private static long StatSize(string path) => JsonDocument.Parse(new ZabbixFileItems().GetValueAsync(ZabbixItemKey.Parse($"vfs.file.get[{path}]", null), Ct).AsTask().GetAwaiter().GetResult().Value!).RootElement.GetProperty("size").GetInt64();

    [Fact]
    public async Task A_directorys_size_on_Windows_is_its_files()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows counts what its files take.");
        string tree = Path.Combine(_root, "tree");
        Write(Path.Combine("tree", "one"), "12345");
        Write(Path.Combine("tree", "sub", "two"), "1234567");
        Assert.Equal("12", await ValueAsync(_files, $"vfs.dir.size[{tree}]"));
        Assert.Equal("5", await ValueAsync(_files, $"vfs.dir.size[{tree},,,,0]"));
        Assert.True(Number(await ValueAsync(_files, $"vfs.dir.size[{tree},,,disk]")) >= 12);
    }

    [Fact]
    public async Task A_cancelled_walk_stops()
    {
        Write("file");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await _files.GetValueAsync(ZabbixItemKey.Parse($"vfs.dir.count[{_root}]", null), cancelled.Token));
    }

    [Fact]
    public async Task A_file_systems_inodes_add_up()
    {
        using var fileSystems = new ZabbixFileSystemItems();
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("Not supported", await ErrorAsync(fileSystems, "vfs.fs.inode[C:]"), StringComparison.Ordinal);
            return;
        }
        ulong total = Number(await ValueAsync(fileSystems, "vfs.fs.inode[/]"));
        // Each read asks the live file system, and APFS's total moves with the files made between two reads.
        ulong margin = total / 100;
        Assert.InRange(Number(await ValueAsync(fileSystems, "vfs.fs.inode[/,total]")), total - margin, total + margin);
        Assert.True(Number(await ValueAsync(fileSystems, "vfs.fs.inode[/,used]")) <= total + margin);
        Assert.True(Number(await ValueAsync(fileSystems, "vfs.fs.inode[/,free]")) <= total + margin);
        double free = Percent(await ValueAsync(fileSystems, "vfs.fs.inode[/,pfree]")), used = Percent(await ValueAsync(fileSystems, "vfs.fs.inode[/,pused]"));
        Assert.InRange(free + used, 99.99, 100.01);
        Assert.Equal("The key's second parameter is not valid.", await ErrorAsync(fileSystems, "vfs.fs.inode[/,most]"));
        Assert.Equal("The key's first parameter is not valid.", await ErrorAsync(fileSystems, "vfs.fs.inode[]"));
        Assert.Equal("The key has more parameters than it takes.", await ErrorAsync(fileSystems, "vfs.fs.inode[/,total,extra]"));
        Assert.StartsWith("The file system's details cannot be read:", await ErrorAsync(fileSystems, "vfs.fs.inode[/no/such/file/system]"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_file_system_list_carries_inodes_where_they_are_counted()
    {
        using var fileSystems = new ZabbixFileSystemItems();
        using JsonDocument document = JsonDocument.Parse(await ValueAsync(fileSystems, "vfs.fs.get"));
        Assert.NotEmpty(document.RootElement.EnumerateArray());
        bool any = document.RootElement.EnumerateArray().Any(entry => entry.TryGetProperty("inodes", out JsonElement inodes)
            && inodes.EnumerateObject().Select(member => member.Name).SequenceEqual(["total", "free", "used", "pfree", "pused"]));
        Assert.Equal(!OperatingSystem.IsWindows(), any);
    }

    [Fact]
    public async Task A_Mac_answers_its_system_keys_from_the_kernel()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "These are the Mach and sysctl calls.");
        var files = new ZabbixHostFiles("/", linux: false);
        using var system = new ZabbixSystemItems(files, TimeProvider.System);

        long boot = (long)Number(await ValueAsync(system, "system.boottime"));
        long up = (long)Number(await ValueAsync(system, "system.uptime"));
        Assert.InRange(boot + up, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5);

        ulong online = Number(await ValueAsync(system, "system.cpu.num"));
        Assert.True(online > 0 && online <= Number(await ValueAsync(system, "system.cpu.num[max]")));
        Assert.True(Percent(await ValueAsync(system, "system.cpu.load")) >= 0);
        Assert.InRange(Percent(await ValueAsync(system, "system.cpu.load[percpu,avg15]")) * online, 0, 100_000);
        Assert.True(Number(await ValueAsync(system, "kernel.maxfiles")) > 0);
        Assert.True(Number(await ValueAsync(system, "kernel.maxproc")) > 0);
        Assert.Contains("Not supported", await ErrorAsync(system, "kernel.openfiles"), StringComparison.Ordinal);

        ulong memory = Number(await ValueAsync(system, "vm.memory.size"));
        Assert.Equal(memory, Number(await ValueAsync(system, "vm.memory.size[total]")));
        Assert.True(memory > 0);
        Assert.True(Number(await ValueAsync(system, "vm.memory.size[used]")) <= memory);
        Assert.True(Number(await ValueAsync(system, "vm.memory.size[wired]")) > 0);
        Assert.True(Number(await ValueAsync(system, "vm.memory.size[active]")) > 0);
        _ = Number(await ValueAsync(system, "vm.memory.size[inactive]"));
        _ = Number(await ValueAsync(system, "vm.memory.size[free]"));
        _ = Number(await ValueAsync(system, "vm.memory.size[available]"));
        Assert.InRange(Percent(await ValueAsync(system, "vm.memory.size[pused]")), 0.001, 100);
        Assert.InRange(Percent(await ValueAsync(system, "vm.memory.size[pavailable]")), 0, 100);
        Assert.Equal("The key's first parameter is not valid.", await ErrorAsync(system, "vm.memory.size[buffers]"));

        ulong swap = Number(await ValueAsync(system, "system.swap.size[all,total]"));
        Assert.True(Number(await ValueAsync(system, "system.swap.size[,used]")) <= swap);
        Assert.True(Number(await ValueAsync(system, "system.swap.size")) <= swap);
        Assert.InRange(Percent(await ValueAsync(system, "system.swap.size[,pfree]")), 0, 100);
        Assert.Equal("The key's first parameter is not valid.", await ErrorAsync(system, "system.swap.size[disk0]"));
        Assert.Contains("Not supported", await ErrorAsync(system, "vm.vmemory.size"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Mac_samples_its_processors()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "This is host_processor_info.");
        using var collector = new ZabbixItemCollector(new ZabbixHostFiles("/", linux: false), TimeProvider.System);
        using var system = new ZabbixSystemItems(collector);
        double idle = Percent(await ValueAsync(system, "system.cpu.util[all,idle]"));
        double busy = Percent(await ValueAsync(system, "system.cpu.util[all,user]")) + Percent(await ValueAsync(system, "system.cpu.util[all,system]"));
        Assert.InRange(idle, 0, 100);
        Assert.InRange(idle + busy, 0, 100.01);
        Assert.InRange(Percent(await ValueAsync(system, "system.cpu.util[0,nice]")), 0, 100);
        Assert.Equal("The key's second parameter is not valid.", await ErrorAsync(system, "system.cpu.util[all,iowait]"));
        Assert.Equal("The processors' figures cannot be read.", await ErrorAsync(system, "system.cpu.util[4096,user]"));
    }

    [Fact]
    public async Task Windows_answers_its_memory_keys_from_the_kernel()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "These are the Win32 calls.");
        using var system = new ZabbixSystemItems();
        ulong memory = Number(await ValueAsync(system, "vm.memory.size"));
        Assert.True(memory > 0);
        Assert.Equal(await ValueAsync(system, "vm.memory.size[free]"), await ValueAsync(system, "vm.memory.size[available]"));
        Assert.True(Number(await ValueAsync(system, "vm.memory.size[used]")) <= memory);
        Assert.InRange(Percent(await ValueAsync(system, "vm.memory.size[pused]")), 0.001, 100);
        Assert.InRange(Percent(await ValueAsync(system, "vm.memory.size[pavailable]")), 0, 100);
        _ = Number(await ValueAsync(system, "vm.memory.size[cached]"));
        Assert.Equal("The key's first parameter is not valid.", await ErrorAsync(system, "vm.memory.size[wired]"));

        ulong commit = Number(await ValueAsync(system, "vm.vmemory.size"));
        Assert.True(Number(await ValueAsync(system, "vm.vmemory.size[used]")) <= commit);
        Assert.InRange(Percent(await ValueAsync(system, "vm.vmemory.size[pused]")), 0, 100);
        Assert.Equal("The key's first parameter is not valid.", await ErrorAsync(system, "vm.vmemory.size[free]"));

        ulong swap = Number(await ValueAsync(system, "system.swap.size"));
        Assert.True(Number(await ValueAsync(system, "system.swap.size[all,used]")) <= swap);
        Assert.InRange(Percent(await ValueAsync(system, "system.swap.size[,pfree]")), 0, 100);
        Assert.Equal("The key's second parameter is not valid.", await ErrorAsync(system, "system.swap.size[,pused]"));
    }

    [Fact]
    public async Task Windows_samples_its_processors_together()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This is GetSystemTimes.");
        using var system = new ZabbixSystemItems();
        Assert.InRange(Percent(await ValueAsync(system, "system.cpu.util")), 0, 100);
        Assert.InRange(Percent(await ValueAsync(system, "system.cpu.util[all,system,avg1]")), 0, 100);
        Assert.Equal("The key's second parameter is not valid.", await ErrorAsync(system, "system.cpu.util[all,user]"));
        Assert.Equal("Windows reports the processors' time only for all of them together.", await ErrorAsync(system, "system.cpu.util[0]"));
    }

    [Theory]
    [InlineData("vmsize")]
    [InlineData("wkset")]
    [InlineData("pf")]
    [InlineData("ktime")]
    [InlineData("utime")]
    [InlineData("gdiobj")]
    [InlineData("userobj")]
    [InlineData("io_read_b")]
    [InlineData("io_read_op")]
    [InlineData("io_write_b")]
    [InlineData("io_write_op")]
    [InlineData("io_other_b")]
    [InlineData("io_other_op")]
    public async Task Windows_answers_every_process_attribute(string attribute)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "proc_info is Windows' key.");
        using Process self = Process.GetCurrentProcess();
        using var processes = new ZabbixProcessItems();
        string image = self.ProcessName + ".exe";
        // The test's own process counts its I/O and memory while it runs, so max and sum come from two moments. Reading max
        // first keeps max <= sum for a counter that only grows; a retry covers one that can shrink between the reads (memory).
        double max = 0, sum = 0;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            max = Percent(await ValueAsync(processes, $"proc_info[{image},{attribute},max]"));
            sum = Percent(await ValueAsync(processes, $"proc_info[{image},{attribute},sum]"));
            if (max <= sum) break;
        }
        Assert.True(sum >= 0);
        Assert.True(max <= sum, $"max {max} is more than sum {sum}");
        if (attribute is "vmsize" or "wkset" or "pf") Assert.True(sum > 0);
    }
}
