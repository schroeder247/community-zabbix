// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net.NetworkInformation;
using System.Reflection;
using Community.Zabbix.Items;

namespace Community.Zabbix.Tests;

/// <summary>
/// The typed catalogue of Zabbix's own keys: an entry's key text is what the server stores and what an agent is asked for,
/// and its type is the one the bundled items' answers parse as. The reflection tests hold every entry, so a key added to
/// the bundled items without an entry here, or an entry whose key nothing answers, fails.
/// </summary>
public sealed class ZabbixItemKeysTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An entry's key, then the text Zabbix's agents know it by.</summary>
    public static TheoryData<string, string> Keys => new()
    {
        { ZabbixItemKeys.Agent.Ping.ToString(), "agent.ping" },
        { ZabbixItemKeys.Agent.Variant.ToString(), "agent.variant" },
        { ZabbixItemKeys.Agent.HostMetadata.ToString(), "agent.hostmetadata" },

        { ZabbixItemKeys.Host.Uname.ToString(), "system.uname" },
        { ZabbixItemKeys.Host.Uptime.ToString(), "system.uptime" },
        { ZabbixItemKeys.Host.BootTime.ToString(), "system.boottime" },
        { ZabbixItemKeys.Host.LocalTime.ToString(), "system.localtime" },
        { ZabbixItemKeys.Host.LocalTimeText.ToString(), "system.localtime[local]" },
        { ZabbixItemKeys.Host.Architecture.ToString(), "system.sw.arch" },
        { ZabbixItemKeys.Host.OsVersion.ToString(), "system.sw.os" },
        { ZabbixItemKeys.Host.OsShortVersion.ToString(), "system.sw.os[short]" },
        { ZabbixItemKeys.Host.OsName.ToString(), "system.sw.os[name]" },
        { ZabbixItemKeys.Host.OsDetails.ToString(), "system.sw.os.get" },
        { ZabbixItemKeys.Host.Hostname().ToString(), "system.hostname" },
        { ZabbixItemKeys.Host.Hostname(ZabbixItemKeys.Host.HostnameKind.ShortHostName).ToString(), "system.hostname[shorthost]" },
        { ZabbixItemKeys.Host.Hostname(ZabbixItemKeys.Host.HostnameKind.FullyQualifiedName, lowercase: true).ToString(), "system.hostname[fqdn,lower]" },
        { ZabbixItemKeys.Host.Hostname(lowercase: true).ToString(), "system.hostname[,lower]" },

        { ZabbixItemKeys.Cpu.Count.ToString(), "system.cpu.num" },
        { ZabbixItemKeys.Cpu.ConfiguredCount.ToString(), "system.cpu.num[max]" },
        { ZabbixItemKeys.Cpu.Discovery.ToString(), "system.cpu.discovery" },
        { ZabbixItemKeys.Cpu.Load().ToString(), "system.cpu.load[all,avg1]" },
        { ZabbixItemKeys.Cpu.Load(ZabbixItemKeys.Cpu.CpuLoadScope.PerProcessor).ToString(), "system.cpu.load[percpu,avg1]" },
        { ZabbixItemKeys.Cpu.Load(window: ZabbixItemKeys.Cpu.CpuLoadWindow.FiveMinutes).ToString(), "system.cpu.load[all,avg5]" },
        { ZabbixItemKeys.Cpu.Load(ZabbixItemKeys.Cpu.CpuLoadScope.PerProcessor, ZabbixItemKeys.Cpu.CpuLoadWindow.FifteenMinutes).ToString(), "system.cpu.load[percpu,avg15]" },
        { ZabbixItemKeys.Cpu.UserPercent().ToString(), "system.cpu.util[,user]" },
        { ZabbixItemKeys.Cpu.SystemPercent(3).ToString(), "system.cpu.util[3,system]" },
        { ZabbixItemKeys.Cpu.IdlePercent(window: ZabbixItemKeys.SampleWindow.FiveMinutes).ToString(), "system.cpu.util[,idle,avg5]" },
        { ZabbixItemKeys.Cpu.GuestNicePercent(0, ZabbixItemKeys.SampleWindow.FifteenMinutes).ToString(), "system.cpu.util[0,guest_nice,avg15]" },
        { ZabbixItemKeys.Cpu.ContextSwitches.ToString(), "system.cpu.switches" },
        { ZabbixItemKeys.Cpu.Interrupts.ToString(), "system.cpu.intr" },
        { ZabbixItemKeys.Cpu.Description().ToString(), "system.hw.cpu" },
        { ZabbixItemKeys.Cpu.Vendor(1).ToString(), "system.hw.cpu[1,vendor]" },
        { ZabbixItemKeys.Cpu.MaxFrequencies.ToString(), "system.hw.cpu[,maxfreq]" },
        { ZabbixItemKeys.Cpu.CurrentFrequencyHertz(2).ToString(), "system.hw.cpu[2,curfreq]" },

        { ZabbixItemKeys.Memory.TotalBytes.ToString(), "vm.memory.size[total]" },
        { ZabbixItemKeys.Memory.AvailableBytes.ToString(), "vm.memory.size[available]" },
        { ZabbixItemKeys.Memory.AvailablePercent.ToString(), "vm.memory.size[pavailable]" },
        { ZabbixItemKeys.Memory.UsedPercent.ToString(), "vm.memory.size[pused]" },
        { ZabbixItemKeys.Memory.WiredBytes.ToString(), "vm.memory.size[wired]" },
        { ZabbixItemKeys.Memory.AnonymousBytes.ToString(), "vm.memory.size[anon]" },
        { ZabbixItemKeys.Memory.VirtualAvailablePercent.ToString(), "vm.vmemory.size[pavailable]" },

        { ZabbixItemKeys.Swap.FreeBytes.ToString(), "system.swap.size[,free]" },
        { ZabbixItemKeys.Swap.UsedPercent.ToString(), "system.swap.size[,pused]" },
        { ZabbixItemKeys.Swap.PagesIn.ToString(), "system.swap.in[,pages]" },
        { ZabbixItemKeys.Swap.OutSectors().ToString(), "system.swap.out[,sectors]" },
        { ZabbixItemKeys.Swap.InOperations("sda2").ToString(), "system.swap.in[sda2,count]" },

        { ZabbixItemKeys.Disk.Discovery.ToString(), "vfs.fs.discovery" },
        { ZabbixItemKeys.Disk.FileSystems.ToString(), "vfs.fs.get" },
        { ZabbixItemKeys.Disk.TotalBytes("/").ToString(), "vfs.fs.size[/,total]" },
        { ZabbixItemKeys.Disk.FreeBytes("/").ToString(), "vfs.fs.size[/,free]" },
        { ZabbixItemKeys.Disk.UsedBytes("/").ToString(), "vfs.fs.size[/,used]" },
        { ZabbixItemKeys.Disk.FreePercent("/").ToString(), "vfs.fs.size[/,pfree]" },
        { ZabbixItemKeys.Disk.UsedPercent("C:").ToString(), "vfs.fs.size[C:,pused]" },
        { ZabbixItemKeys.Disk.InodesFreePercent("/var").ToString(), "vfs.fs.inode[/var,pfree]" },
        { ZabbixItemKeys.Disk.InodesTotal("/var").ToString(), "vfs.fs.inode[/var,total]" },
        { ZabbixItemKeys.Disk.DeviceDiscovery.ToString(), "vfs.dev.discovery" },
        { ZabbixItemKeys.Disk.ReadSectorsPerSecond().ToString(), "vfs.dev.read[,sps]" },
        { ZabbixItemKeys.Disk.ReadOperationsPerSecond("sda", ZabbixItemKeys.SampleWindow.FiveMinutes).ToString(), "vfs.dev.read[sda,ops,avg5]" },
        { ZabbixItemKeys.Disk.WriteSectors("sda").ToString(), "vfs.dev.write[sda,sectors]" },
        { ZabbixItemKeys.Disk.WriteOperations().ToString(), "vfs.dev.write[,operations]" },

        { ZabbixItemKeys.File.Exists("/etc/passwd").ToString(), "vfs.file.exists[/etc/passwd]" },
        { ZabbixItemKeys.File.Exists("/p", ZabbixItemKeys.File.FileTypes.Directory).ToString(), "vfs.file.exists[/p,dir]" },
        { ZabbixItemKeys.File.Exists("/p", ZabbixItemKeys.File.FileTypes.File | ZabbixItemKeys.File.FileTypes.SymbolicLink).ToString(), "vfs.file.exists[/p,\"file,sym\"]" },
        { ZabbixItemKeys.File.Exists("/p", exclude: ZabbixItemKeys.File.FileTypes.Device).ToString(), "vfs.file.exists[/p,,dev]" },
        { ZabbixItemKeys.File.Exists("/p", ZabbixItemKeys.File.FileTypes.All).ToString(), "vfs.file.exists[/p,all]" },
        { ZabbixItemKeys.File.SizeBytes("/p").ToString(), "vfs.file.size[/p,bytes]" },
        { ZabbixItemKeys.File.LineCount("/p").ToString(), "vfs.file.size[/p,lines]" },
        { ZabbixItemKeys.File.ModifiedTime("/p").ToString(), "vfs.file.time[/p,modify]" },
        { ZabbixItemKeys.File.ChangedTime("/p").ToString(), "vfs.file.time[/p,change]" },
        { ZabbixItemKeys.File.OwnerName("/p").ToString(), "vfs.file.owner[/p,user,name]" },
        { ZabbixItemKeys.File.GroupId("/p").ToString(), "vfs.file.owner[/p,group,id]" },
        { ZabbixItemKeys.File.Details("/p").ToString(), "vfs.file.get[/p]" },
        { ZabbixItemKeys.File.Contents("/p").ToString(), "vfs.file.contents[/p]" },
        { ZabbixItemKeys.File.Contents("/p", "UTF-16").ToString(), "vfs.file.contents[/p,UTF-16]" },
        { ZabbixItemKeys.File.FirstMatchingLine("/p", "^root").ToString(), "vfs.file.regexp[/p,^root]" },
        { ZabbixItemKeys.File.FirstMatchingLine("/p", "^(a|b),", startLine: 2, output: "\\1").ToString(), "vfs.file.regexp[/p,\"^(a|b),\",,2,,\\1]" },
        { ZabbixItemKeys.File.HasMatchingLine("/p", "[0-9]+", endLine: 9).ToString(), "vfs.file.regmatch[/p,\"[0-9]+\",,,9]" },
        { ZabbixItemKeys.File.Md5Sum("/p").ToString(), "vfs.file.md5sum[/p]" },
        { ZabbixItemKeys.File.Crc32Checksum("/p").ToString(), "vfs.file.cksum[/p,crc32]" },
        { ZabbixItemKeys.File.Sha256Checksum("/p").ToString(), "vfs.file.cksum[/p,sha256]" },
        { ZabbixItemKeys.File.Permissions("/p").ToString(), "vfs.file.permissions[/p]" },
        { ZabbixItemKeys.File.DirectoryEntryCount("/d").ToString(), "vfs.dir.count[/d]" },
        { ZabbixItemKeys.File.DirectoryEntryCount("/d", includeTypes: ZabbixItemKeys.File.FileTypes.File, maxDepth: 1).ToString(), "vfs.dir.count[/d,,,file,,1]" },
        {
            ZabbixItemKeys.File.DirectoryEntries("/d", "a", "b", ZabbixItemKeys.File.FileTypes.Directory, ZabbixItemKeys.File.FileTypes.Fifo, 2, 10, 20, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1), "c").ToString(),
            "vfs.dir.get[/d,a,b,dir,fifo,2,10,20,60,3600,c]"
        },
        { ZabbixItemKeys.File.DirectorySizeBytes("/d").ToString(), "vfs.dir.size[/d]" },
        { ZabbixItemKeys.File.DirectorySizeBytes("/d", maxDepth: 1).ToString(), "vfs.dir.size[/d,,,,1]" },
        { ZabbixItemKeys.File.DirectoryDiskBytes("/d").ToString(), "vfs.dir.size[/d,,,disk]" },

        { ZabbixItemKeys.Process.Count().ToString(), "proc.num" },
        { ZabbixItemKeys.Process.Count("nginx").ToString(), "proc.num[nginx]" },
        { ZabbixItemKeys.Process.Count(state: ZabbixItemKeys.Process.ProcessState.Running).ToString(), "proc.num[,,run]" },
        { ZabbixItemKeys.Process.Count("nginx", "www", commandLine: "worker").ToString(), "proc.num[nginx,www,all,worker]" },
        { ZabbixItemKeys.Process.MemoryBytes().ToString(), "proc.mem" },
        { ZabbixItemKeys.Process.MemoryBytes(memoryType: ZabbixItemKeys.Process.ProcessMemoryType.ResidentSet).ToString(), "proc.mem[,,sum,,rss]" },
        { ZabbixItemKeys.Process.MemoryBytes("nginx", statistic: ZabbixItemKeys.Process.ProcessMemoryStatistic.Maximum).ToString(), "proc.mem[nginx,,max]" },
        { ZabbixItemKeys.Process.AverageMemoryBytes("nginx").ToString(), "proc.mem[nginx,,avg]" },
        { ZabbixItemKeys.Process.MemoryPercent().ToString(), "proc.mem[,,sum,,pmem]" },
        { ZabbixItemKeys.Process.AverageMemoryPercent("nginx").ToString(), "proc.mem[nginx,,avg,,pmem]" },
        { ZabbixItemKeys.Process.CpuPercent().ToString(), "proc.cpu.util" },
        { ZabbixItemKeys.Process.CpuPercent(window: ZabbixItemKeys.SampleWindow.FiveMinutes).ToString(), "proc.cpu.util[,,total,,avg5]" },
        { ZabbixItemKeys.Process.CpuPercent("nginx", time: ZabbixItemKeys.Process.ProcessCpuTime.Privileged).ToString(), "proc.cpu.util[nginx,,system]" },
        { ZabbixItemKeys.Process.Details().ToString(), "proc.get" },
        { ZabbixItemKeys.Process.ThreadDetails().ToString(), "proc.get[,,,thread]" },
        { ZabbixItemKeys.Process.Summary("nginx").ToString(), "proc.get[nginx,,,summary]" },
        { ZabbixItemKeys.Process.Info("explorer.exe").ToString(), "proc_info[explorer.exe]" },
        { ZabbixItemKeys.Process.Info("explorer.exe", ZabbixItemKeys.Process.ProcessInfoCounter.WorkingSetKilobytes, ZabbixItemKeys.Process.ProcessInfoStatistic.Sum).ToString(), "proc_info[explorer.exe,wkset,sum]" },

        { ZabbixItemKeys.Kernel.MaxFiles.ToString(), "kernel.maxfiles" },
        { ZabbixItemKeys.Kernel.MaxProcesses.ToString(), "kernel.maxproc" },
        { ZabbixItemKeys.Kernel.OpenFiles.ToString(), "kernel.openfiles" },

        { ZabbixItemKeys.Network.InterfaceDiscovery.ToString(), "net.if.discovery" },
        { ZabbixItemKeys.Network.InterfaceList.ToString(), "net.if.list" },
        { ZabbixItemKeys.Network.BytesReceived("eth0").ToString(), "net.if.in[eth0]" },
        { ZabbixItemKeys.Network.MulticastReceived("eth0").ToString(), "net.if.in[eth0,multicast]" },
        { ZabbixItemKeys.Network.BytesSent("eth0").ToString(), "net.if.out[eth0]" },
        { ZabbixItemKeys.Network.SendCarrierErrors("eth0").ToString(), "net.if.out[eth0,carrier]" },
        { ZabbixItemKeys.Network.TotalPackets("eth0").ToString(), "net.if.total[eth0,packets]" },
        { ZabbixItemKeys.Network.Collisions("eth0").ToString(), "net.if.collisions[eth0]" },
        { ZabbixItemKeys.Network.MacAddresses().ToString(), "system.hw.macaddr" },
        { ZabbixItemKeys.Network.MacAddresses("^eth", addressesOnly: true).ToString(), "system.hw.macaddr[^eth,short]" },
        { ZabbixItemKeys.Network.IsTcpListening(80).ToString(), "net.tcp.listen[80]" },
        { ZabbixItemKeys.Network.IsUdpListening(68).ToString(), "net.udp.listen[68]" },
        { ZabbixItemKeys.Network.TcpSocketCount().ToString(), "net.tcp.socket.count" },
        { ZabbixItemKeys.Network.TcpSocketCount(localPort: 80, state: TcpState.Established).ToString(), "net.tcp.socket.count[,80,,,established]" },
        { ZabbixItemKeys.Network.TcpSocketCount(state: TcpState.SynReceived).ToString(), "net.tcp.socket.count[,,,,syn_recv]" },
        { ZabbixItemKeys.Network.UdpSocketCount(localPort: 68).ToString(), "net.udp.socket.count[,68]" },
        { ZabbixItemKeys.Network.UdpSocketCount(state: ZabbixItemKeys.Network.UdpSocketState.Unconnected).ToString(), "net.udp.socket.count[,,,,unconn]" },
        { ZabbixItemKeys.Network.IsTcpPortOpen(80).ToString(), "net.tcp.port[,80]" },
        { ZabbixItemKeys.Network.IsTcpPortOpen(80, "10.0.0.1").ToString(), "net.tcp.port[10.0.0.1,80]" },
        { ZabbixItemKeys.Network.IsTcpServiceUp(ZabbixItemKeys.Network.TcpService.Ssh).ToString(), "net.tcp.service[ssh]" },
        { ZabbixItemKeys.Network.IsTcpServiceUp(ZabbixItemKeys.Network.TcpService.Tcp, port: 8080).ToString(), "net.tcp.service[tcp,,8080]" },
        { ZabbixItemKeys.Network.TcpServiceResponseTime(ZabbixItemKeys.Network.TcpService.Https, "10.0.0.1", 8443).ToString(), "net.tcp.service.perf[https,10.0.0.1,8443]" },
        { ZabbixItemKeys.Network.IsNtpUp().ToString(), "net.udp.service[ntp]" },
        { ZabbixItemKeys.Network.NtpResponseTime("10.0.0.1").ToString(), "net.udp.service.perf[ntp,10.0.0.1]" },
        { ZabbixItemKeys.Network.DnsResolves("example.com").ToString(), "net.dns[,example.com]" },
        { ZabbixItemKeys.Network.DnsResolves("example.com", recordType: ZabbixItemKeys.Network.DnsRecordType.Ipv6Address).ToString(), "net.dns[,example.com,AAAA]" },
        { ZabbixItemKeys.Network.DnsResponseTime("example.com", "10.0.0.2", ZabbixItemKeys.Network.DnsRecordType.Address, TimeSpan.FromSeconds(2), 3, useTcp: true).ToString(), "net.dns.perf[10.0.0.2,example.com,A,2,3,tcp]" },
        { ZabbixItemKeys.Network.DnsRecords("example.com", useTcp: true).ToString(), "net.dns.record[,example.com,SOA,,,tcp]" },
        { ZabbixItemKeys.Network.ServerStatistics().ToString(), "zabbix.stats" },
        { ZabbixItemKeys.Network.ServerStatistics("10.0.0.1", 10051).ToString(), "zabbix.stats[10.0.0.1,10051]" },
        { ZabbixItemKeys.Network.ServerQueue().ToString(), "zabbix.stats[,,queue]" },
        { ZabbixItemKeys.Network.ServerQueue("10.0.0.1", 10051, TimeSpan.FromSeconds(6), TimeSpan.FromMinutes(1)).ToString(), "zabbix.stats[10.0.0.1,10051,queue,6,60]" },

        { ZabbixItemKeys.Web.Page("localhost").ToString(), "web.page.get[localhost]" },
        { ZabbixItemKeys.Web.Page("localhost", "index.html", 8080).ToString(), "web.page.get[localhost,index.html,8080]" },
        { ZabbixItemKeys.Web.PageLoadTime("localhost", port: 80).ToString(), "web.page.perf[localhost,,80]" },
        { ZabbixItemKeys.Web.PageMatch("localhost", "OK", port: 80).ToString(), "web.page.regexp[localhost,,80,OK]" },
        { ZabbixItemKeys.Web.PageMatch("localhost", "OK", length: 10, output: "\\0").ToString(), "web.page.regexp[localhost,,,OK,10,\\0]" },
        { ZabbixItemKeys.Web.Certificate("example.com").ToString(), "web.certificate.get[example.com]" },
        { ZabbixItemKeys.Web.Certificate("example.com", 8443, "10.0.0.1").ToString(), "web.certificate.get[example.com,8443,10.0.0.1]" },
    };

    [Theory]
    [MemberData(nameof(Keys))]
    public void An_entry_writes_the_key_Zabbix_knows(string actual, string expected) => Assert.Equal(expected, actual);

    [Fact]
    public void Every_entry_with_no_parameters_has_a_key_that_parses()
    {
        List<(string Member, ZabbixItemKey Key)> properties = [.. PropertyEntries()];

        Assert.NotEmpty(properties);
        Assert.All(properties, entry =>
        {
            Assert.True(ZabbixItemKey.TryParse(entry.Key.ToString(), out ZabbixItemKey parsed), entry.Member);
            Assert.Equal(entry.Key, parsed);
            Assert.NotEqual(string.Empty, parsed.Name);
        });
    }

    /// <summary>A key's name written wrong compiles and fails only on the agent, so the agent's own table reads every entry's.</summary>
    [Fact]
    public void Every_entry_names_a_key_the_built_in_table_answers()
    {
        ZabbixItemDictionary table = ZabbixItemDictionary.BuiltIn("web-01");

        Assert.All(PropertyEntries().Concat(MethodEntries()), entry => Assert.True(table.ContainsKey(entry.Key.Name), $"{entry.Member}: {entry.Key}"));
    }

    [Fact]
    public void Every_key_the_built_in_table_answers_has_an_entry()
    {
        HashSet<string> named = [.. PropertyEntries().Concat(MethodEntries()).Select(entry => entry.Key.Name)];

        List<string> missing = [.. ZabbixItemDictionary.BuiltIn("web-01").Keys.Where(key => !named.Contains(key)).Order(StringComparer.Ordinal)];

        Assert.Empty(missing);
    }

    /// <summary>The mode decides the type: each value is one the implementation writes for that key.</summary>
    [Fact]
    public void An_entry_reads_the_text_its_key_answers_with()
    {
        Assert.True(ZabbixItemKeys.Agent.Ping.Parse("1"));
        Assert.Equal(2L, ZabbixItemKeys.Agent.Variant.Parse("2"));
        Assert.Equal(TimeSpan.FromHours(1), ZabbixItemKeys.Host.Uptime.Parse("3600"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), ZabbixItemKeys.Host.BootTime.Parse("1700000000"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), ZabbixItemKeys.Host.LocalTime.Parse("1700000000"));
        Assert.Equal("2026-11-04,09:30:00.000,+01:00", ZabbixItemKeys.Host.LocalTimeText.Parse("2026-11-04,09:30:00.000,+01:00"));
        Assert.Equal(1.5, ZabbixItemKeys.Cpu.Load().Parse("1.500000"));
        Assert.Equal(12.5, ZabbixItemKeys.Cpu.UserPercent().Parse("12.500000"));
        Assert.Equal(18_446_744_073_709_551_615UL, ZabbixItemKeys.Cpu.ContextSwitches.Parse("18446744073709551615"));
        Assert.Equal(3_400_000_000L, ZabbixItemKeys.Cpu.MaxFrequencyHertz(0).Parse("3400000000"));
        Assert.Equal("processor 0: 3400MHz", ZabbixItemKeys.Cpu.MaxFrequencies.Parse("processor 0: 3400MHz"));
        Assert.Equal(8_192L, ZabbixItemKeys.Memory.AvailableBytes.Parse("8192"));
        Assert.Equal(50.0, ZabbixItemKeys.Memory.AvailablePercent.Parse("50.000000"));
        Assert.Equal(1_024L, ZabbixItemKeys.Swap.FreeBytes.Parse("1024"));
        Assert.Equal(25.0, ZabbixItemKeys.Swap.FreePercent.Parse("25.000000"));
        Assert.Equal(1_024L, ZabbixItemKeys.Disk.FreeBytes("/").Parse("1024"));
        Assert.Equal(25.0, ZabbixItemKeys.Disk.FreePercent("/").Parse("25.000000"));
        Assert.Equal(100UL, ZabbixItemKeys.Disk.ReadSectors().Parse("100"));
        Assert.Equal(2.5, ZabbixItemKeys.Disk.ReadSectorsPerSecond().Parse("2.500000"));
        Assert.False(ZabbixItemKeys.File.Exists("/p").Parse("0"));
        Assert.True(ZabbixItemKeys.File.HasMatchingLine("/p", "x").Parse("1"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), ZabbixItemKeys.File.ModifiedTime("/p").Parse("1700000000"));
        Assert.Equal("S-1-5-18", ZabbixItemKeys.File.OwnerId("C:\\p").Parse("S-1-5-18"));
        Assert.Equal("0644", ZabbixItemKeys.File.Permissions("/p").Parse("0644"));
        Assert.Equal(4_294_967_295L, ZabbixItemKeys.File.Crc32Checksum("/p").Parse("4294967295"));
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", ZabbixItemKeys.File.Md5Checksum("/p").Parse("d41d8cd98f00b204e9800998ecf8427e"));
        Assert.Equal(4_096L, ZabbixItemKeys.Process.MemoryBytes().Parse("4096"));
        Assert.Equal(4_096.5, ZabbixItemKeys.Process.AverageMemoryBytes().Parse("4096.500000"));
        Assert.Equal(1.25, ZabbixItemKeys.Process.MemoryPercent().Parse("1.250000"));
        Assert.Equal(4.0, ZabbixItemKeys.Process.Info("x.exe").Parse("4.000000"));
        Assert.Equal(TimeSpan.FromSeconds(0.25), ZabbixItemKeys.Network.TcpServiceResponseTime(ZabbixItemKeys.Network.TcpService.Ssh).Parse("0.250000"));
        Assert.Equal(TimeSpan.Zero, ZabbixItemKeys.Network.DnsResponseTime("x").Parse("0.000000"));
        Assert.Equal("""{"response":"success","data":{"queue":3}}""", ZabbixItemKeys.Network.ServerQueue().Parse("""{"response":"success","data":{"queue":3}}"""));
        Assert.Equal(TimeSpan.FromSeconds(0.5), ZabbixItemKeys.Web.PageLoadTime("x").Parse("0.500000"));
    }

    /// <summary>The bundled items' real answers, for keys every platform has, parse as their entries say.</summary>
    [Fact]
    public async Task An_entry_parses_what_the_bundled_items_answer()
    {
        using var system = new ZabbixSystemItems();
        using var fileSystems = new ZabbixFileSystemItems();
        string fileSystem = (Path.GetPathRoot(Path.GetTempPath()) ?? "/").TrimEnd('\\');

        Assert.True(ZabbixItemKeys.Memory.TotalBytes.Parse(await Answer(system, ZabbixItemKeys.Memory.TotalBytes.Key)) > 0);
        Assert.InRange(ZabbixItemKeys.Memory.AvailablePercent.Parse(await Answer(system, ZabbixItemKeys.Memory.AvailablePercent.Key)), 0.0, 100.0);
        Assert.True(ZabbixItemKeys.Host.Uptime.Parse(await Answer(system, ZabbixItemKeys.Host.Uptime.Key)) > TimeSpan.Zero);
        Assert.True(ZabbixItemKeys.Host.BootTime.Parse(await Answer(system, ZabbixItemKeys.Host.BootTime.Key)).Year > 2000);
        Assert.True(ZabbixItemKeys.Host.LocalTime.Parse(await Answer(system, ZabbixItemKeys.Host.LocalTime.Key)).Year > 2000);
        Assert.Contains(",", ZabbixItemKeys.Host.LocalTimeText.Parse(await Answer(system, ZabbixItemKeys.Host.LocalTimeText.Key)), StringComparison.Ordinal);
        Assert.True(ZabbixItemKeys.Cpu.Count.Parse(await Answer(system, ZabbixItemKeys.Cpu.Count.Key)) > 0);
        Assert.True(ZabbixItemKeys.Disk.TotalBytes(fileSystem).Parse(await Answer(fileSystems, ZabbixItemKeys.Disk.TotalBytes(fileSystem).Key)) > 0);
        Assert.InRange(ZabbixItemKeys.Disk.UsedPercent(fileSystem).Parse(await Answer(fileSystems, ZabbixItemKeys.Disk.UsedPercent(fileSystem).Key)), 0.0, 100.0);
    }

    [Fact]
    public void A_key_with_a_number_or_a_text_no_key_accepts_is_refused()
    {
        Assert.Throws<ArgumentNullException>("path", () => ZabbixItemKeys.Disk.FreeBytes(null!));
        Assert.Throws<ArgumentException>("path", () => ZabbixItemKeys.Disk.FreeBytes(string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>("port", () => ZabbixItemKeys.Network.IsTcpListening(65_536));
        Assert.Throws<ArgumentOutOfRangeException>("port", () => ZabbixItemKeys.Network.IsTcpListening(-1));
        Assert.Throws<ArgumentOutOfRangeException>("processor", () => ZabbixItemKeys.Cpu.UserPercent(-1));
        Assert.Throws<ArgumentOutOfRangeException>("startLine", () => ZabbixItemKeys.File.HasMatchingLine("/p", "x", startLine: 0));
        Assert.Throws<ArgumentOutOfRangeException>("timeout", () => ZabbixItemKeys.Network.DnsResolves("x", timeout: TimeSpan.FromMilliseconds(500)));
        Assert.Throws<ArgumentOutOfRangeException>("attempts", () => ZabbixItemKeys.Network.DnsResolves("x", attempts: 0));
        Assert.Throws<ArgumentOutOfRangeException>("scope", () => ZabbixItemKeys.Cpu.Load((ZabbixItemKeys.Cpu.CpuLoadScope)99));
        Assert.Throws<ArgumentOutOfRangeException>("window", () => ZabbixItemKeys.Cpu.UserPercent(window: (ZabbixItemKeys.SampleWindow)99));
        Assert.Throws<ArgumentOutOfRangeException>("state", () => ZabbixItemKeys.Network.TcpSocketCount(state: TcpState.DeleteTcb));
        Assert.Throws<ArgumentOutOfRangeException>("include", () => ZabbixItemKeys.File.Exists("/p", (ZabbixItemKeys.File.FileTypes)1024));
        Assert.Throws<ArgumentException>("port", () => ZabbixItemKeys.Network.IsTcpServiceUp(ZabbixItemKeys.Network.TcpService.Tcp));
        Assert.Throws<FormatException>(() => ZabbixItemKeys.File.Details("C:\\a,b\\"));
    }

    private static async Task<string> Answer(ZabbixItemProvider provider, ZabbixItemKey key)
    {
        ZabbixAgentAnswer answer = await provider.GetValueAsync(key, Ct);
        Assert.True(answer.IsSupported, $"{key}: {answer.Error}");
        return Assert.IsType<string>(answer.Value);
    }

    private static IEnumerable<Type> Groups()
        => typeof(ZabbixItemKeys).GetNestedTypes(BindingFlags.Public).Where(type => type is { IsAbstract: true, IsSealed: true });

    private static IEnumerable<(string Member, ZabbixItemKey Key)> PropertyEntries()
        => Groups().SelectMany(group => group.GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(property => ($"{group.Name}.{property.Name}", KeyOf(property.GetValue(null)))));

    /// <summary>Each method called with its required arguments alone: text, a number, or an enumeration's first member.</summary>
    private static IEnumerable<(string Member, ZabbixItemKey Key)> MethodEntries()
        => Groups().SelectMany(group => group.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => ($"{group.Name}.{method.Name}", KeyOf(method.Invoke(null, [.. method.GetParameters().Select(Argument)])))));

    private static object? Argument(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue) return parameter.DefaultValue;
        Type type = parameter.ParameterType;
        if (type == typeof(string)) return "x";
        if (type == typeof(int)) return 1;
        Assert.True(type.IsEnum, $"{parameter.Member.Name}.{parameter.Name} is required and of a type this test cannot fill.");
        return Activator.CreateInstance(type);
    }

    private static ZabbixItemKey KeyOf(object? item)
    {
        Assert.NotNull(item);
        Assert.Equal(typeof(ZabbixItemKey<>), item.GetType().GetGenericTypeDefinition());
        return Assert.IsType<ZabbixItemKey>(item.GetType().GetProperty(nameof(ZabbixItemKey<int>.Key))?.GetValue(item));
    }
}
