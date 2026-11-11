// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Items;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The bundled items (docs/adr/0009-bundled-items.md): Zabbix's own keys, with Zabbix's parameters, values and texts.
/// The Linux readers run over a fixture tree of <c>/proc</c>, <c>/sys</c> and <c>/etc</c> files, so they are held on
/// every system the tests run on; the file, network and web keys run against real files and loopback servers.
/// </summary>
public sealed class BundledItemsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"zbx-items-{Guid.NewGuid():N}");
    private readonly ZabbixHostFiles _files;
    private readonly ZabbixItemCollector _collector;
    private readonly ZabbixSystemItems _system;
    private readonly ZabbixProcessItems _processes;
    private readonly ZabbixFileSystemItems _fileSystems;

    public BundledItemsTests()
    {
        _files = new ZabbixHostFiles(_root, linux: true);
        _collector = new ZabbixItemCollector(_files, TimeProvider.System);
        _system = new ZabbixSystemItems(_collector);
        _processes = new ZabbixProcessItems(_collector);
        _fileSystems = new ZabbixFileSystemItems(_collector);
        Write("/proc/meminfo", "MemTotal:       16000000 kB\nMemFree:         2000000 kB\nMemAvailable:    8000000 kB\nBuffers:          500000 kB\nCached:          4000000 kB\nActive:          6000000 kB\nInactive:        3000000 kB\nAnonPages:       2500000 kB\nSlab:             700000 kB\nSwapTotal:       4000000 kB\nSwapFree:        3000000 kB\n");
        Write("/proc/loadavg", "0.52 1.04 2.08 3/412 9999\n");
        Write("/proc/uptime", "12345.67 98765.43\n");
        Write("/proc/stat", "cpu  1 2 3 4\nintr 77777 1 2 3\nctxt 55555\nbtime 1700000000\nprocesses 9\n");
        Write("/proc/vmstat", "nr_free_pages 1\npswpin 321\npswpout 654\n");
        Write("/proc/sys/fs/file-max", "9223372036854775807\n");
        Write("/proc/sys/kernel/pid_max", "4194304\n");
        Write("/proc/sys/fs/file-nr", "2048\t0\t9223372036854775807\n");
        Write("/proc/sys/kernel/osrelease", "6.8.0-45-generic\n");
        Write("/proc/version", "Linux version 6.8.0-45-generic (buildd@lcy02) #45-Ubuntu SMP\n");
        Write("/etc/os-release", "NAME=\"Ubuntu\"\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\nID=ubuntu\n");
        Write("/sys/devices/system/cpu/online", "0-2,5\n");
        foreach (int cpu in new[] { 0, 1, 2, 3, 4, 5 }) Directory.CreateDirectory(_files.Resolve($"/sys/devices/system/cpu/cpu{cpu}"));
        Directory.CreateDirectory(_files.Resolve("/sys/devices/system/cpu/cpufreq"));
        Write("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq", "3600000\n");
        Write("/proc/cpuinfo", "processor\t: 0\nvendor_id\t: GenuineIntel\nmodel name\t: Intel(R) Xeon(R)\ncpu MHz\t\t: 2400.000\n\nprocessor\t: 1\nvendor_id\t: GenuineIntel\nmodel name\t: Intel(R) Xeon(R)\ncpu MHz\t\t: 1200.500\n");
        Write("/etc/passwd", "root:x:0:0:root:/root:/bin/bash\nwww-data:x:33:33:www-data:/var/www:/usr/sbin/nologin\n");
        Write("/etc/group", "root:x:0:\nwww-data:x:33:\n");
        Process(1, "systemd", "S (sleeping)", 0, 0, "/sbin/init\0splash\0", rss: 12000, vsize: 160000);
        Process(42, "nginx", "R (running)", 33, 1, "/usr/sbin/nginx\0-g\0daemon off;\0", rss: 8000, vsize: 50000);
        Process(43, "nginx", "S (sleeping)", 33, 42, "nginx: worker process\0", rss: 4000, vsize: 50000);
        Write("/proc/net/dev", "Inter-|   Receive                                                |  Transmit\n face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed\n    lo: 1000 10 1 2 3 4 5 6 2000 20 7 8 9 10 11 12\n  eth0: 5000 50 0 0 0 0 0 9 7000 70 0 0 0 1 0 0\n");
        Write("/proc/diskstats", "   8       0 sda 100 0 2000 0 50 0 1000 0 0 0 0\n   8       1 sda1 10 0 200 0 5 0 100 0 0 0 0\n");
        Write("/sys/class/block/sda/uevent", "MAJOR=8\nMINOR=0\nDEVNAME=sda\nDEVTYPE=disk\n");
        Write("/sys/class/block/sda1/uevent", "MAJOR=8\nMINOR=1\nDEVNAME=sda1\nDEVTYPE=partition\n");
    }

    public void Dispose()
    {
        _system.Dispose();
        _processes.Dispose();
        _fileSystems.Dispose();
        _collector.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- system, kernel, memory ----

    [Theory]
    [InlineData("vm.memory.size", "16384000000")]
    [InlineData("vm.memory.size[total]", "16384000000")]
    [InlineData("vm.memory.size[free]", "2048000000")]
    [InlineData("vm.memory.size[used]", "14336000000")]
    [InlineData("vm.memory.size[pused]", "87.500000")]
    [InlineData("vm.memory.size[available]", "8192000000")]
    [InlineData("vm.memory.size[pavailable]", "50.000000")]
    [InlineData("vm.memory.size[buffers]", "512000000")]
    [InlineData("vm.memory.size[cached]", "4096000000")]
    [InlineData("vm.memory.size[slab]", "716800000")]
    [InlineData("system.cpu.load", "0.520000")]
    [InlineData("system.cpu.load[all,avg5]", "1.040000")]
    [InlineData("system.cpu.load[percpu,avg15]", "0.520000")]
    [InlineData("system.cpu.num", "4")]
    [InlineData("system.cpu.num[max]", "6")]
    [InlineData("system.cpu.switches", "55555")]
    [InlineData("system.cpu.intr", "77777")]
    [InlineData("system.uptime", "12345")]
    [InlineData("system.boottime", "1700000000")]
    [InlineData("kernel.maxfiles", "9223372036854775807")]
    [InlineData("kernel.maxproc", "4194304")]
    [InlineData("kernel.openfiles", "2048")]
    [InlineData("system.swap.size", "3072000000")]
    [InlineData("system.swap.size[all,total]", "4096000000")]
    [InlineData("system.swap.size[,pfree]", "75.000000")]
    [InlineData("system.swap.size[,pused]", "25.000000")]
    [InlineData("system.swap.in", "321")]
    [InlineData("system.swap.out[all,pages]", "654")]
    [InlineData("system.sw.os[name]", "Ubuntu 24.04.1 LTS")]
    [InlineData("system.sw.os", "Linux version 6.8.0-45-generic (buildd@lcy02) #45-Ubuntu SMP")]
    [InlineData("system.hw.cpu[0,curfreq]", "2400000000")]
    [InlineData("system.hw.cpu[0,maxfreq]", "3600000000")]
    [InlineData("system.hw.cpu[all,vendor]", "processor 0: GenuineIntel\nprocessor 1: GenuineIntel")]
    [InlineData("system.hw.cpu", "processor 0: GenuineIntel Intel(R) Xeon(R) working at 2400MHz (maximum 3600MHz)\nprocessor 1: GenuineIntel Intel(R) Xeon(R) working at 1200MHz")]
    public async Task A_system_key_answers_as_Zabbix_s_agent_reads_the_kernel_s_files(string key, string value)
    {
        Assert.Equal(value, await ValueAsync(_system, key));
    }

    [Theory]
    [InlineData("vm.memory.size[shared]", "This kernel does not report shared memory this way.")]
    [InlineData("vm.memory.size[bogus]", "The key's first parameter is not valid.")]
    [InlineData("vm.memory.size[total,x]", "The key has more parameters than it takes.")]
    [InlineData("system.cpu.load[all,avg2]", "The key's second parameter is not valid.")]
    [InlineData("system.swap.size[/dev/sda2]", "The key's first parameter is not valid.")]
    [InlineData("system.swap.in[sda2]", "The key's second parameter is not valid.")]
    [InlineData("system.cpu.num[all]", "The key's first parameter is not valid.")]
    public async Task A_system_key_refuses_a_parameter_with_Zabbix_s_text(string key, string error)
    {
        Assert.Equal(error, await ErrorAsync(_system, key));
    }

    [Fact]
    public async Task The_processor_discovery_lists_each_processor_with_its_state()
    {
        string json = await ValueAsync(_system, "system.cpu.discovery");

        Assert.Equal("""[{"{#CPU.NUMBER}":0,"{#CPU.STATUS}":"online"},{"{#CPU.NUMBER}":1,"{#CPU.STATUS}":"online"},{"{#CPU.NUMBER}":2,"{#CPU.STATUS}":"online"},{"{#CPU.NUMBER}":3,"{#CPU.STATUS}":"offline"},{"{#CPU.NUMBER}":4,"{#CPU.STATUS}":"offline"},{"{#CPU.NUMBER}":5,"{#CPU.STATUS}":"online"}]""", json);
    }

    [Fact]
    public async Task The_operating_system_s_details_are_Zabbix_s_fields()
    {
        using JsonDocument json = JsonDocument.Parse(await ValueAsync(_system, "system.sw.os.get"));
        JsonElement os = json.RootElement;

        Assert.Equal("linux", os.GetProperty("os_type").GetString());
        Assert.Equal("Ubuntu 24.04.1 LTS", os.GetProperty("product_name").GetString());
        Assert.Equal("6", os.GetProperty("kernel_major").GetString());
        Assert.Equal("8", os.GetProperty("kernel_minor").GetString());
        Assert.Equal("0", os.GetProperty("kernel_patch").GetString());
        Assert.Equal("6.8.0-45-generic", os.GetProperty("kernel").GetString());
        Assert.StartsWith("Ubuntu 24.04.1 LTS ", os.GetProperty("version_pretty").GetString(), StringComparison.Ordinal);
        Assert.Equal("Linux version 6.8.0-45-generic (buildd@lcy02) #45-Ubuntu SMP", os.GetProperty("version_full").GetString());
    }

    [Fact]
    public async Task The_local_time_is_the_agent_s_clock_in_Zabbix_s_two_forms()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 18, 30, 15, 250, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        using var items = new ZabbixSystemItems(_files, time);

        Assert.Equal("1793730615", await ValueAsync(items, "system.localtime"));
        Assert.Equal("2026-11-03,18:30:15.250,+00:00", await ValueAsync(items, "system.localtime[local]"));
        Assert.Equal("The key's first parameter is not valid.", await ErrorAsync(items, "system.localtime[gmt]"));
    }

    [Fact]
    public async Task The_host_name_is_lowercased_and_shortened_as_asked()
    {
        ZabbixSystemItems items = _system;

        string lower = await ValueAsync(items, "system.hostname[host,lower]");
        Assert.Equal(Environment.MachineName, lower, ignoreCase: true);
        Assert.DoesNotContain(lower, char.IsUpper);
        Assert.DoesNotContain(".", await ValueAsync(items, "system.hostname[shorthost]"), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows()) Assert.Equal("Only Windows has a NetBIOS name.", await ErrorAsync(items, "system.hostname[netbios]"));
        Assert.Equal("The key's second parameter is not valid.", await ErrorAsync(items, "system.hostname[host,upper]"));
    }

    // ---- processes ----

    [Theory]
    [InlineData("proc.num", "3")]
    [InlineData("proc.num[nginx]", "2")]
    [InlineData("proc.num[systemd]", "1")]
    [InlineData("proc.num[init]", "1")]                                                 // the command's own name, as argv[0] gives it
    [InlineData("proc.num[,www-data]", "2")]
    [InlineData("proc.num[,nobody-at-all]", "0")]                                       // a user that does not exist owns none
    [InlineData("proc.num[nginx,,run]", "1")]
    [InlineData("proc.num[nginx,,sleep]", "1")]
    [InlineData("proc.num[,,,worker]", "1")]
    [InlineData("proc.mem[nginx]", "102400000")]
    [InlineData("proc.mem[nginx,,sum,,rss]", "12288000")]
    [InlineData("proc.mem[nginx,,max,,rss]", "8192000")]
    [InlineData("proc.mem[nginx,,avg,,rss]", "6144000.000000")]
    [InlineData("proc.mem[nginx,,sum,,pmem]", "0.075000")]
    [InlineData("proc.mem[nothing-runs-this]", "0")]
    public async Task A_process_key_matches_by_name_user_state_and_command_line(string key, string value)
    {
        Assert.Equal(value, await ValueAsync(_processes, key));
    }

    [Fact]
    public async Task The_process_list_carries_Zabbix_s_fields_and_minus_one_for_what_cannot_be_read()
    {
        using JsonDocument json = JsonDocument.Parse(await ValueAsync(_processes, "proc.get[nginx]"));
        JsonElement master = json.RootElement.EnumerateArray().Single(process => process.GetProperty("pid").GetInt32() == 42);

        Assert.Equal(2, json.RootElement.GetArrayLength());
        Assert.Equal(1, master.GetProperty("ppid").GetInt32());
        Assert.Equal("nginx", master.GetProperty("name").GetString());
        Assert.Equal("/usr/sbin/nginx -g daemon off;", master.GetProperty("cmdline").GetString());
        Assert.Equal("www-data", master.GetProperty("user").GetString());
        Assert.Equal("www-data", master.GetProperty("group").GetString());
        Assert.Equal(8192000, master.GetProperty("rss").GetInt64());
        Assert.Equal("running", master.GetProperty("state").GetString());
        Assert.Equal(1.5, master.GetProperty("cputime_user").GetDouble());
        Assert.Equal(-1, master.GetProperty("pss").GetInt64());
    }

    [Fact]
    public async Task The_process_summary_sums_each_name()
    {
        using JsonDocument json = JsonDocument.Parse(await ValueAsync(_processes, "proc.get[,,,summary]"));
        JsonElement nginx = json.RootElement.EnumerateArray().Single(process => process.GetProperty("name").GetString() == "nginx");

        Assert.Equal(2, nginx.GetProperty("processes").GetInt32());
        Assert.Equal(12288000, nginx.GetProperty("rss").GetInt64());
        Assert.Equal("The key's fourth parameter is not valid.", await ErrorAsync(_processes, "proc.get[,,nginx,summary]"));
    }

    // ---- file systems and block devices ----

    [Fact]
    public async Task A_file_system_s_space_adds_up_as_Zabbix_s_does()
    {
        ZabbixFileSystemItems items = _fileSystems;
        string root = OperatingSystem.IsWindows() ? Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\') : "/";

        ulong total = ulong.Parse(await ValueAsync(items, $"vfs.fs.size[{root}]"), CultureInfo.InvariantCulture);
        ulong free = ulong.Parse(await ValueAsync(items, $"vfs.fs.size[{root},free]"), CultureInfo.InvariantCulture);
        ulong used = ulong.Parse(await ValueAsync(items, $"vfs.fs.size[{root},used]"), CultureInfo.InvariantCulture);
        double pfree = double.Parse(await ValueAsync(items, $"vfs.fs.size[{root},pfree]"), CultureInfo.InvariantCulture);
        double pused = double.Parse(await ValueAsync(items, $"vfs.fs.size[{root},pused]"), CultureInfo.InvariantCulture);

        Assert.True(total > 0 && used <= total && free <= total);
        Assert.Equal(100.0, pfree + pused, precision: 3);
        Assert.Equal("The key's second parameter is not valid.", await ErrorAsync(items, $"vfs.fs.size[{root},half]"));
    }

    [Fact]
    public async Task The_mounts_are_discovered_with_their_type_and_options()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A Linux mount point is not a drive to DriveInfo on Windows.");
        Write("/proc/mounts", "/dev/sda1 / ext4 rw,relatime 0 0\nproc /proc proc rw,nosuid 0 0\n");

        string json = await ValueAsync(_fileSystems, "vfs.fs.discovery");

        Assert.Equal("""[{"{#FSNAME}":"/","{#FSTYPE}":"ext4","{#FSOPTIONS}":"rw,relatime"},{"{#FSNAME}":"/proc","{#FSTYPE}":"proc","{#FSOPTIONS}":"rw,nosuid"}]""", json);
    }

    [Theory]
    [InlineData("vfs.dev.read[sda,sectors]", "2000")]
    [InlineData("vfs.dev.read[/dev/sda,operations]", "100")]
    [InlineData("vfs.dev.write[sda1,sectors]", "100")]
    [InlineData("vfs.dev.write[all,operations]", "55")]
    public async Task A_block_device_s_counters_are_proc_diskstats(string key, string value)
    {
        Assert.Equal(value, await ValueAsync(_fileSystems, key));
    }

    [Fact]
    public async Task The_block_devices_are_discovered_with_their_type()
    {
        Assert.Equal("""[{"{#DEVNAME}":"sda","{#DEVTYPE}":"disk"},{"{#DEVNAME}":"sda1","{#DEVTYPE}":"partition"}]""", await ValueAsync(_fileSystems, "vfs.dev.discovery"));
    }

    // ---- files ----

    [Fact]
    public async Task A_file_s_size_lines_contents_and_checksums_are_Zabbix_s()
    {
        string file = Write("/data/sample.txt", "123456789");
        string lines = Write("/data/lines.txt", "﻿first line\r\nroot:x:0:0\n\nlast\r\n\r\n");
        var items = new ZabbixFileItems();

        Assert.Equal("1", await ValueAsync(items, $"vfs.file.exists[{file}]"));
        Assert.Equal("0", await ValueAsync(items, $"vfs.file.exists[{file}.missing]"));
        Assert.Equal("1", await ValueAsync(items, $"vfs.file.exists[{Path.GetDirectoryName(file)},dir]"));
        Assert.Equal("0", await ValueAsync(items, $"vfs.file.exists[{Path.GetDirectoryName(file)}]"));
        Assert.Equal("9", await ValueAsync(items, $"vfs.file.size[{file}]"));
        Assert.Equal("5", await ValueAsync(items, $"vfs.file.size[{lines},lines]"));
        Assert.Equal("930766865", await ValueAsync(items, $"vfs.file.cksum[{file}]"));                // printf 123456789 | cksum
        Assert.Equal("25f9e794323b453885f5181f1b624d0b", await ValueAsync(items, $"vfs.file.md5sum[{file}]"));
        Assert.Equal("15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225", await ValueAsync(items, $"vfs.file.cksum[{file},sha256]"));
        Assert.Equal("first line\r\nroot:x:0:0\n\nlast", await ValueAsync(items, $"vfs.file.contents[{lines}]"));
        Assert.Equal("The key's second parameter is not valid.", await ErrorAsync(items, $"vfs.file.size[{file},words]"));
    }

    [Fact]
    public async Task A_line_search_answers_the_first_match_through_the_output_template()
    {
        string file = Write("/data/passwd", "root:x:0:0:root:/root:/bin/bash\nwww-data:x:33:33:www-data:/var/www:/usr/sbin/nologin\n");
        var items = new ZabbixFileItems();

        Assert.Equal("root:x:0:0:root:/root:/bin/bash", await ValueAsync(items, $"vfs.file.regexp[{file},^root]"));
        Assert.Equal("33 /var/www", await ValueAsync(items, $@"vfs.file.regexp[{file},""^www-data:x:([0-9]+):[0-9]+:[^:]*:([^:]*)"",,,,""\1 \2""]"));
        Assert.Equal(string.Empty, await ValueAsync(items, $"vfs.file.regexp[{file},^root,,2]"));
        Assert.Equal("1", await ValueAsync(items, $"vfs.file.regmatch[{file},nologin]"));
        Assert.Equal("0", await ValueAsync(items, $"vfs.file.regmatch[{file},nologin,,1,1]"));
        Assert.Equal("The start line comes after the end line.", await ErrorAsync(items, $"vfs.file.regexp[{file},root,,3,2]"));
    }

    [Fact]
    public async Task A_file_s_time_and_permissions_are_the_file_system_s()
    {
        string file = Write("/data/stamped", "x");
        var modified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, modified);
        var items = new ZabbixFileItems();

        Assert.Equal(new DateTimeOffset(modified).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), await ValueAsync(items, $"vfs.file.time[{file}]"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Assert.Equal("0640", await ValueAsync(items, $"vfs.file.permissions[{file}]"));
        }
    }

    [Fact]
    public async Task A_directory_s_entries_are_counted_by_name_type_size_and_depth()
    {
        Write("/tree/a.log", "1234");
        Write("/tree/b.txt", "1");
        Write("/tree/sub/c.log", "123456789");
        Write("/tree/sub/deeper/d.log", "12");
        string tree = _files.Resolve("/tree");
        var items = new ZabbixFileItems();

        Assert.Equal("6", await ValueAsync(items, $"vfs.dir.count[{tree}]"));                      // 4 files, 2 directories
        Assert.Equal("4", await ValueAsync(items, $"vfs.dir.count[{tree},,,file]"));
        Assert.Equal("3", await ValueAsync(items, $@"vfs.dir.count[{tree},""\.log$""]"));
        Assert.Equal("2", await ValueAsync(items, $"vfs.dir.count[{tree},,,,dir,0]"));             // files of the top level only
        Assert.Equal("2", await ValueAsync(items, $"vfs.dir.count[{tree},,,file,,,4]"));           // at least 4 bytes
        Assert.Equal("1", await ValueAsync(items, $@"vfs.dir.count[{tree},"".log$"",,file,,,,,,,""^sub$""]"));
    }

    // ---- network ----

    [Theory]
    [InlineData("net.if.in[lo]", "1000")]
    [InlineData("net.if.in[lo,packets]", "10")]
    [InlineData("net.if.in[lo,dropped]", "2")]
    [InlineData("net.if.in[lo,multicast]", "6")]
    [InlineData("net.if.out[lo,bytes]", "2000")]
    [InlineData("net.if.out[lo,collisions]", "10")]
    [InlineData("net.if.out[lo,carrier]", "11")]
    [InlineData("net.if.total[lo]", "3000")]
    [InlineData("net.if.total[lo,errors]", "8")]
    [InlineData("net.if.collisions[eth0]", "1")]
    public async Task An_interface_s_counters_are_proc_net_dev(string key, string value)
    {
        Assert.Equal(value, await ValueAsync(new ZabbixNetworkItems(_files, TimeProvider.System), key));
    }

    [Fact]
    public async Task The_interfaces_are_discovered_and_an_unknown_one_is_named()
    {
        var items = new ZabbixNetworkItems(_files, TimeProvider.System);

        Assert.Equal("""[{"{#IFNAME}":"lo"},{"{#IFNAME}":"eth0"}]""", await ValueAsync(items, "net.if.discovery"));
        Assert.Equal("/proc/net/dev does not list this network interface.", await ErrorAsync(items, "net.if.in[wlan9]"));
        Assert.Equal("The key's second parameter is not valid.", await ErrorAsync(items, "net.if.total[lo,carrier]"));
    }

    [Fact]
    public async Task A_port_and_a_listener_are_seen_as_they_are()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var items = new ZabbixNetworkItems();

        Assert.Equal("1", await ValueAsync(items, $"net.tcp.port[127.0.0.1,{port}]"));
        Assert.Equal("1", await ValueAsync(items, $"net.tcp.service[tcp,127.0.0.1,{port}]"));
        Assert.Equal("1", await ValueAsync(items, $"net.tcp.listen[{port}]"));
        listener.Stop();
        Assert.Equal("0", await ValueAsync(items, $"net.tcp.port[127.0.0.1,{port}]"));
        Assert.Equal("0.000000", await ValueAsync(items, $"net.tcp.service.perf[tcp,127.0.0.1,{port}]"));
        Assert.Equal("The key's third parameter is not valid.", await ErrorAsync(items, "net.tcp.service[tcp]"));
        Assert.Equal("The SDK has no LDAP client to check with.", await ErrorAsync(items, "net.tcp.service[ldap]"));
    }

    [Theory]
    [InlineData("smtp", "220 mail.example ESMTP ready\r\n", "1")]
    [InlineData("smtp", "220-first\r\n220 second\r\n", "1")]
    [InlineData("smtp", "554 go away\r\n", "0")]
    [InlineData("ftp", "220 FTP ready\r\n", "1")]
    [InlineData("pop", "+OK POP3 ready\r\n", "1")]
    [InlineData("pop", "-ERR\r\n", "0")]
    [InlineData("imap", "* OK IMAP ready\r\n", "1")]
    [InlineData("nntp", "200 news ready\r\n", "1")]
    [InlineData("ssh", "SSH-2.0-OpenSSH_9.6\r\n", "1")]
    [InlineData("ssh", "not ssh\r\n", "0")]
    [InlineData("telnet", "\xff\xfd\x18login: ", "1")]
    public async Task A_service_check_reads_the_protocol_s_greeting(string service, string greeting, string up)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string answered = await AskWhileServingAsync(listener, Encoding.Latin1.GetBytes(greeting), () => ValueAsync(new ZabbixNetworkItems(), $"net.tcp.service[{service},127.0.0.1,{port}]"));

        Assert.Equal(up, answered);
    }

    [Fact]
    public async Task A_name_resolves_through_the_system_resolver()
    {
        var items = new ZabbixNetworkItems();

    }

    // ---- web ----

    [Fact]
    public async Task A_page_is_its_status_line_headers_and_body()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 23\r\nConnection: close\r\n\r\nstatus: ok\nversion: 7\n\n");
        var items = new ZabbixWebItems();

        string page = await AskWhileServingAsync(listener, response, () => ValueAsync(items, $"web.page.get[127.0.0.1,/health,{port}]"), readRequest: true);
        string version = await AskWhileServingAsync(listener, response, () => ValueAsync(items, $@"web.page.regexp[127.0.0.1,/health,{port},""version: ([0-9]+)"",,""v\1""]"), readRequest: true);

        Assert.StartsWith("HTTP/1.1 200 OK\r\n", page, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\nstatus: ok\nversion: 7\n\n", page, StringComparison.Ordinal);
        Assert.Equal("v7", version);
        Assert.Equal("The key has the wrong number of parameters for this type.", await ErrorAsync(items, $"web.page.regexp[127.0.0.1,/,{port}]"));
    }

    [Fact]
    public async Task A_self_signed_certificate_is_reported_as_one()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task serving = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(Ct);
            await using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsServerAsync(certificate);
        }, Ct);

        using JsonDocument json = JsonDocument.Parse(await ValueAsync(new ZabbixWebItems(), $"web.certificate.get[localhost,{port},127.0.0.1]"));
        await serving;
        JsonElement root = json.RootElement;

        Assert.Equal("valid-but-self-signed", root.GetProperty("result").GetProperty("value").GetString());
        Assert.Equal("CN=localhost", root.GetProperty("x509").GetProperty("subject").GetString());
        Assert.Equal("SHA256-RSA", root.GetProperty("x509").GetProperty("signature_algorithm").GetString());
        Assert.Equal("RSA", root.GetProperty("x509").GetProperty("public_key_algorithm").GetString());
        Assert.Equal(["localhost"], root.GetProperty("x509").GetProperty("alternative_names").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal(new DateTimeOffset(certificate.NotAfter).ToUnixTimeSeconds(), root.GetProperty("x509").GetProperty("not_after").GetProperty("timestamp").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)), root.GetProperty("sha256_fingerprint").GetString());
    }

    // ---- the agent's table ----

    [Fact]
    public async Task Every_agent_answers_the_bundled_keys_beside_the_built_in_four()
    {
        ZabbixItemDictionary handlers = ZabbixItemDictionary.BuiltIn("edge-01");

        Assert.Equal("2", (await handlers.AnswerAsync(Key("agent.variant"), TimeSpan.FromSeconds(3), Ct)).Value);
        Assert.Equal(string.Empty, (await handlers.AnswerAsync(Key("agent.hostmetadata"), TimeSpan.FromSeconds(3), Ct)).Value);
        Assert.True(handlers.ContainsKey("vfs.fs.size"));
        Assert.True(handlers.ContainsKey("proc.num"));
        Assert.True(handlers.ContainsKey("web.page.get"));
        Assert.False(handlers.ContainsKey("system.run"));
    }

    [Fact]
    public async Task A_provider_s_key_replaces_a_bundled_one_and_the_bundled_set_can_be_left_out()
    {
        ZabbixItemDictionary with = new ZabbixItemDictionary().Add(new FixedSize());
        with.AddDefaults("edge-01", "linux web", bundled: true, _collector);
        var without = new ZabbixItemDictionary();
        without.AddDefaults("edge-01", null, bundled: false, _collector);

        Assert.Equal("42", (await with.AnswerAsync(Key("vfs.fs.size[/]"), TimeSpan.FromSeconds(3), Ct)).Value);
        Assert.Equal("linux web", (await with.AnswerAsync(Key("agent.hostmetadata"), TimeSpan.FromSeconds(3), Ct)).Value);
        Assert.Equal("This agent does not answer the key.", (await without.AnswerAsync(Key("vfs.fs.size[/]"), TimeSpan.FromSeconds(3), Ct)).Error);
        Assert.Equal("1", (await without.AnswerAsync(Key("agent.ping"), TimeSpan.FromSeconds(3), Ct)).Value);
    }

    [Fact]
    public async Task A_passive_agent_answers_bundled_keys_its_rules_allow()
    {
        await using ZabbixPassiveAgent agent = ZabbixPassiveAgent.CreateBuilder("edge-01")
            .AllowPeer(IPAddress.Loopback)
            .Configure(o =>
            {
                o.Address = "127.0.0.1";
                o.Port = 0;
                o.KeyRules.Add("AllowKey=agent.*");
                o.KeyRules.Add("AllowKey=vfs.fs.*[*]");
                o.KeyRules.Add("DenyKey=*");
            })
            .Build();
        await agent.StartAsync(Ct);
        int port = ((IPEndPoint)agent.Listener!.LocalEndPoint!).Port;
        using var getter = new ZabbixGetter($"127.0.0.1:{port}");

        Assert.Equal("2", (await getter.GetAsync("agent.variant", Ct)).Value);
        Assert.True((await getter.GetAsync(OperatingSystem.IsWindows() ? "vfs.fs.size[C:,pfree]" : "vfs.fs.size[/,pfree]", Ct)).IsSupported);
        Assert.Equal("This agent does not answer the key.", (await getter.GetAsync("vfs.fs.discovery", Ct)).Error);   // vfs.fs.*[*] needs brackets
        Assert.Equal("This agent does not answer the key.", (await getter.GetAsync("proc.num", Ct)).Error);
    }

    private sealed class FixedSize : ZabbixItemProvider
    {
        public override IReadOnlyList<ZabbixItemDefinition> Items { get; } = [new("vfs.fs.size", "A fixed size")];

        public override ValueTask<ZabbixAgentAnswer> GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken)
            => ValueTask.FromResult(ZabbixAgentAnswer.Supported(key, "42"));
    }

    // The checker's answer, once the one connection it made has been served and closed.
    private static async Task<string> AskWhileServingAsync(TcpListener listener, byte[] reply, Func<Task<string>> ask, bool readRequest = false)
    {
        Task serving = ServeOnceAsync(listener, reply, readRequest);
        try
        {
            return await ask();
        }
        finally
        {
            await serving;
        }
    }

    private static async Task ServeOnceAsync(TcpListener listener, byte[] greeting, bool readRequest = false)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(Ct);
        NetworkStream stream = client.GetStream();
        if (readRequest)
        {
            byte[] request = new byte[4096];
            int read = 0;
            while (read < 4 || !Encoding.ASCII.GetString(request, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal))
                read += await stream.ReadAsync(request.AsMemory(read), Ct);
        }
        await stream.WriteAsync(greeting, Ct);
        await stream.FlushAsync(Ct);
        // Read until the checker hangs up (QUIT, its SSH identification, or nothing), so the reply is not cut short.
        byte[] rest = new byte[1024];
        try
        {
            while (await stream.ReadAsync(rest, Ct) > 0) { }
        }
        catch (IOException)
        {
        }
    }

    private void Process(int pid, string name, string state, int uid, int ppid, string cmdline, int rss, int vsize)
    {
        Write($"/proc/{pid}/status", $"Name:\t{name}\nState:\t{state}\nPPid:\t{ppid}\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\nGid:\t{uid}\t{uid}\t{uid}\t{uid}\nVmSize:\t{vsize} kB\nVmRSS:\t{rss} kB\nThreads:\t1\nvoluntary_ctxt_switches:\t5\nnonvoluntary_ctxt_switches:\t2\n");
        Write($"/proc/{pid}/cmdline", cmdline);
        Write($"/proc/{pid}/stat", $"{pid} ({name}) {state[0]} {ppid} 0 0 0 0 0 0 0 7 0 150 25 0 0 20 0 1 0 100\n");
    }

    private string Write(string path, string text)
    {
        string resolved = _files.Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        File.WriteAllText(resolved, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return resolved;
    }

    private static ZabbixItemKey Key(string text) => ZabbixItemKey.Parse(text);

    // Each key within a deadline, as an agent asks it: a service that never answers is down, not a hung test.
    [Fact]
    public async Task A_mac_address_pattern_that_cannot_be_read_is_the_key_s_reason()
        => Assert.StartsWith("The first parameter is not a valid regular expression", await ErrorAsync(_system, "system.hw.macaddr[(]"), StringComparison.Ordinal);

    private static async Task<string> ValueAsync(ZabbixItemProvider items, string key)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        ZabbixAgentAnswer answer = await items.GetValueAsync(Key(key), deadline.Token);
        Assert.True(answer.IsSupported, $"{key}: {answer.Error}");
        return answer.Value!;
    }

    private static async Task<string?> ErrorAsync(ZabbixItemProvider items, string key)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        ZabbixAgentAnswer answer = await items.GetValueAsync(Key(key), deadline.Token);
        Assert.False(answer.IsSupported, $"{key} answered {answer.Value}");
        return answer.Error;
    }
}
