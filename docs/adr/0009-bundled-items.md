<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# 0009. Bundled items: Zabbix's agent keys, answered from the .NET libraries

- **Status:** Accepted; amended 2026-11-03 (what a template gets, the reasons' wording and corrections; below)
- **Date:** 2026-11-03

## Context

Every agent answers four keys: `agent.ping`, `agent.version`, `agent.hostname` and `system.uname`
(`ZabbixItemHandlers.BuiltIn`). Every other key is the application's own, through a handler, a provider or a plugin
([ADR 0008](0008-item-providers-and-plugins.md)). Zabbix's own agents answer about ninety keys, and Zabbix's templates
for Linux and Windows hosts are built on them: `system.cpu.util`, `vm.memory.size`, `vfs.fs.size`, `net.if.in`,
`proc.num` and the rest. An SDK agent with four keys cannot stand in for a Zabbix agent on a host, or serve those
templates, unless the application implements each key itself.

### What Zabbix's agents answer

| Family | Keys |
| --- | --- |
| Agent | `agent.hostmetadata`, `agent.hostname`, `agent.ping`, `agent.variant`, `agent.version` |
| System | `system.boottime`, `system.cpu.discovery`, `system.cpu.intr`, `system.cpu.load`, `system.cpu.num`, `system.cpu.switches`, `system.cpu.util`, `system.hostname`, `system.hw.chassis`, `system.hw.cpu`, `system.hw.devices`, `system.hw.macaddr`, `system.localtime`, `system.run`, `system.stat`, `system.sw.arch`, `system.sw.os`, `system.sw.os.get`, `system.sw.packages`, `system.sw.packages.get`, `system.swap.in`, `system.swap.out`, `system.swap.size`, `system.uname`, `system.uptime`, `system.users.num`, `sensor` |
| Kernel | `kernel.maxfiles`, `kernel.maxproc`, `kernel.openfiles` |
| Memory | `vm.memory.size`; on Windows `vm.vmemory.size` |
| Processes | `proc.cpu.util`, `proc.get`, `proc.mem`, `proc.num`; on Windows `proc_info` |
| File systems and devices | `vfs.fs.discovery`, `vfs.fs.get`, `vfs.fs.inode`, `vfs.fs.size`, `vfs.dev.discovery`, `vfs.dev.read`, `vfs.dev.write` |
| Files and directories | `vfs.file.cksum`, `vfs.file.contents`, `vfs.file.exists`, `vfs.file.get`, `vfs.file.md5sum`, `vfs.file.owner`, `vfs.file.permissions`, `vfs.file.regexp`, `vfs.file.regmatch`, `vfs.file.size`, `vfs.file.time`, `vfs.dir.count`, `vfs.dir.get`, `vfs.dir.size` |
| Network | `net.if.collisions`, `net.if.discovery`, `net.if.in`, `net.if.out`, `net.if.total`, `net.tcp.listen`, `net.tcp.port`, `net.tcp.service`, `net.tcp.service.perf`, `net.tcp.socket.count`, `net.udp.listen`, `net.udp.service`, `net.udp.service.perf`, `net.udp.socket.count`, `net.dns`, `net.dns.perf`, `net.dns.record`; on Windows `net.if.list` |
| Web | `web.page.get`, `web.page.perf`, `web.page.regexp`; agent 2 also `web.certificate.get` |
| Log files (active checks only) | `log`, `log.count`, `logrt`, `logrt.count`; on Windows `eventlog`, `eventlog.count` |
| Windows | `perf_counter`, `perf_counter_en`, `perf_instance.discovery`, `perf_instance_en.discovery`, `registry.data`, `registry.get`, `service.discovery`, `service.info`, `services`, `wmi.get`, `wmi.getall` |
| Other | `zabbix.stats`, `modbus.get` |

Zabbix's agents decide which keys may be asked with `AllowKey` and `DenyKey`: ordered patterns with `*` wildcards, the
first match deciding, and everything allowed that no rule denies. Since 5.0.2 the default configuration is
`DenyKey=system.run[*]`.

### What .NET offers

The two shared frameworks hold 335 public namespaces in 245 assemblies: 127 namespaces in `Microsoft.NETCore.App`
10.0, which every application runs on, and the rest in `Microsoft.AspNetCore.App`, which only `Zabbix.Proxy`
references (for its listeners). These are the namespaces that measure a host:

| Namespace (assembly) | What it gives | Keys it serves |
| --- | --- | --- |
| `System` (`Environment`, `OperatingSystem`, `DateTimeOffset`) | host name, processor count, OS version, the monotonic tick count, the local time and offset | `system.hostname`, `system.cpu.num`, `system.localtime`, `system.uptime` on Windows |
| `System.Runtime.InteropServices` (`RuntimeInformation`) | the OS description and architecture; `LibraryImport` for documented OS functions | `system.uname`, `system.sw.arch`, `system.sw.os` (portable part); §6 |
| `System.Diagnostics` (`Process`) | every process's name, id, threads, handles, working set, virtual size, CPU times and start time | `proc.num`, `proc.mem`, `proc.cpu.util`, `proc.get`, `proc_info` |
| `System.IO` (`DriveInfo`) | each mounted file system's name, type, label, total size, free space and space available to the caller | `vfs.fs.discovery`, `vfs.fs.size`, `vfs.fs.get` (bytes) |
| `System.IO`, `System.IO.Enumeration` (`File`, `FileInfo`, `Directory`, `FileSystemEnumerable<T>`, `EnumerationOptions`) | existence, size, times, Unix permissions (`File.GetUnixFileMode`), link targets, contents, filtered recursive enumeration | `vfs.file.*`, `vfs.dir.*` |
| `System.IO` (`File` on Linux's `/proc` and `/sys`) | the kernel's own figures, as the C agent reads them | `system.cpu.util`, `system.cpu.load`, `vm.memory.size`, `system.swap.*`, `kernel.*`, `vfs.dev.*`, `system.hw.cpu`, process details |
| `System.Security.Cryptography` (`MD5`, `SHA256`) | file digests | `vfs.file.md5sum`, `vfs.file.cksum[,md5|sha256]` |
| `System.Security.AccessControl` (`FileSystemAclExtensions`) | a file's owner and group on Windows | `vfs.file.owner`, `vfs.file.get` on Windows |
| `System.Text.RegularExpressions`, `System.Text` (`CodePagesEncodingProvider`) | Zabbix's regular-expression parameters; the encodings its file keys name | `vfs.file.regexp`, `vfs.file.regmatch`, `vfs.file.contents`, `web.page.regexp` |
| `System.Net.NetworkInformation` (`NetworkInterface`, `IPInterfaceStatistics`, `IPGlobalProperties`) | interfaces, MAC addresses, per-interface byte, packet, error and discard counters; TCP and UDP listeners and TCP connections | `net.if.*`, `system.hw.macaddr`, `net.tcp.listen`, `net.udp.listen`, `net.tcp.socket.count`, `net.udp.socket.count` |
| `System.Net.Sockets` (`TcpClient`, `UdpClient`, `Socket`) | connections, protocol greetings, UDP exchanges | `net.tcp.port`, `net.tcp.service*`, `net.udp.service*` (NTP), `net.dns*` (the SDK's RFC 1035 client) |
| `System.Net` (`Dns`) | the system resolver's A and AAAA answers, and nothing else: no named server, no other record type, so the DNS keys do not use it | `system.hostname` on Windows |
| `System.Net.Http` (`HttpClient`, `SocketsHttpHandler`) | a page's status, headers, body and load time | `web.page.get`, `web.page.perf`, `web.page.regexp` |
| `System.Net.Security`, `System.Security.Cryptography.X509Certificates` (`SslStream`, `X509Certificate2`, `X509Chain`) | a server's certificate and its validation | `web.certificate.get`, `net.tcp.service[https]` |
| `Microsoft.Win32` (`Registry`) | the Windows registry | `registry.data`, `registry.get`, `system.sw.os` on Windows |
| `Community.Zabbix.Protocol` (this SDK's wire) | the `zabbix.stats` request | `zabbix.stats[ip,port]` |

Microsoft ships the rest of Windows' sources as packages: `System.Diagnostics.PerformanceCounter` (`perf_counter*`,
`perf_instance*.discovery`), `System.ServiceProcess.ServiceController` (`service.*`, `services`), `System.Management`
(`wmi.get`, `wmi.getall`, and a process's user and command line on Windows) and `System.Diagnostics.EventLog`
(`eventlog`, `eventlog.count`; it is also in `Microsoft.AspNetCore.App`, which an agent does not reference). Nothing in either framework reads a host's hardware tables
(`system.hw.chassis`, `system.hw.devices`, `sensor`), its package databases (`system.sw.packages*`), its login records
(`system.users.num`) or a Modbus bus.

Measured on macOS (osx-arm64, .NET 10.0.11), the portable APIs answer: `Environment`, `RuntimeInformation`, `Dns`,
`DriveInfo`, `NetworkInterface`, `IPGlobalProperties`, `Process`, `File`, the hashes and the code pages. There are two
exceptions: the interface statistics have no outgoing-discard counter, and the CPU times of other accounts' processes
need root.

## Decision

### 1. The principle: Zabbix's keys, Zabbix's values

The bundled items answer Zabbix's own keys, with Zabbix's parameters, modes, units and JSON shapes, so Zabbix's
templates work unchanged. A key, parameter or mode that a platform cannot answer the way Zabbix's agent does is answered
as not supported, with the reason; it is never approximated. Each key's reference page lists its platforms and every
difference from Zabbix's agent.

### 2. Where they live

| Package | Holds | New dependencies |
| --- | --- | --- |
| `Community.Zabbix.Agent` | every key the shared framework and the OS's own files answer, in public `ZabbixItemProvider`s by family, in the namespace `Community.Zabbix.Items`: `ZabbixSystemItems`, `ZabbixProcessItems`, `ZabbixFileSystemItems`, `ZabbixFileItems`, `ZabbixNetworkItems`, `ZabbixWebItems` | none |
| `Community.Zabbix.Agent.Windows` (new) | `registry.*`, `perf_counter*`, `perf_instance*.discovery`, `service.*`, `services`, `wmi.*`, `eventlog*`, and the Windows parts of `proc.*` that need WMI | `System.Diagnostics.PerformanceCounter`, `System.ServiceProcess.ServiceController`, `System.Management`, `System.Diagnostics.EventLog` |

The family providers are public so a custom handler or provider can answer from them, and so an application that turns
the bundled set off (§3) can add back one family with `AddItems`.

### 3. On by default, below every other key

The bundled keys join the four in the table's lowest tier (`ZabbixItemHandlers.Compose`): an application's handler and
a provider's key both take the name first, so a provider that lists `vfs.fs.size` replaces the bundled one without
failing the start. Both agents answer the bundled set unless their options turn it off (`BundledItems = false`).

Both agents' options gain `KeyRules`, Zabbix's `AllowKey` and `DenyKey` lines in one ordered list (the order decides,
so the two kinds are one list), with Zabbix's matching (`*` wildcards, first match, allowed by default) and its
finalization (`ZabbixKeyRules`). They hold for every key the agent answers, not only the bundled ones, live in the
agent's handler table (`ZabbixItemHandlers.KeyRules`) so every dispatch path applies them, and are bound from
configuration like the other options (`Zabbix:ActiveAgents:<name>:KeyRules`). `system.run` is not bundled: running commands
is ADR 0008's "Command items", a later ADR.

### 4. The catalogue

Waves are the order of work (§7). "OS files" means Linux's `/proc` and `/sys`, read with `System.IO`; "platform call"
means a documented OS function through `LibraryImport` (§6).

| Key | Source | Linux | Windows | macOS | Wave |
| --- | --- | --- | --- | --- | --- |
| `agent.ping`, `agent.version`, `agent.hostname` | today's | ✓ | ✓ | ✓ | – |
| `agent.variant` | `ZabbixWire.AgentVariant` | ✓ | ✓ | ✓ | 1 |
| `agent.hostmetadata` | the active agent's `HostMetadata`; empty for a passive agent | ✓ | ✓ | ✓ | 1 |
| `system.uname` | today's (`RuntimeInformation.OSDescription`) | ✓ | ✓ | ✓ | – |
| `system.hostname[<type>,<transform>]` | `Environment.MachineName`, `Dns.GetHostName`, `Dns.GetHostEntry` | ✓ | ✓ | ✓ | 1 |
| `system.localtime[<type>]` | `DateTimeOffset` | ✓ | ✓ | ✓ | 1 |
| `system.uptime`, `system.boottime` | OS files (`/proc/uptime`); `Environment.TickCount64`; platform call (`sysctl kern.boottime`: the tick count stops while a Mac sleeps) | ✓ | ✓ | 3 | 1 |
| `system.cpu.num[<type>]`, `system.cpu.discovery` | OS files (`/sys/devices/system/cpu`); `Environment.ProcessorCount` (the process's own limit, so on Windows `max` is not supported); platform call (`sysctl hw.activecpu`, `hw.ncpu`) | ✓ | ✓ | ✓, 3 | 1 |
| `system.cpu.load[<cpu>,<mode>]` | OS files (`/proc/loadavg`); platform call (`getloadavg`) | ✓ | 4 | 3 | 1 |
| `system.cpu.util[<cpu>,<type>,<mode>]` | a collector over OS files (`/proc/stat`); platform calls (`GetSystemTimes`, `host_processor_info`) | 2 | 3 | 3 | 2 |
| `system.cpu.switches`, `system.cpu.intr` | OS files (`/proc/stat`) | ✓ | – | – | 1 |
| `system.sw.arch` | `RuntimeInformation.OSArchitecture`, in `uname`'s names | ✓ | ✓ | ✓ | 1 |
| `system.sw.os[<info>]`, `system.sw.os.get` | OS files (`/proc/version`, `/etc/os-release`); `Registry`; `RuntimeInformation` | ✓ | ✓ | ✓ | 1 |
| `system.swap.size[<device>,<type>]` | OS files (`/proc/meminfo`, `/proc/swaps`); platform calls (`GlobalMemoryStatusEx`, `sysctl vm.swapusage`) | ✓ | 3 | 3 | 1 |
| `system.swap.in`, `system.swap.out` | OS files (`/proc/vmstat`, `/proc/swaps`, `/proc/diskstats`) | ✓ | – | – | 1 |
| `system.hw.macaddr[<interface>,<format>]` | `NetworkInterface.GetPhysicalAddress` | ✓ | ✓ | ✓ | 1 |
| `system.hw.cpu[<cpu>,<info>]` | OS files (`/proc/cpuinfo`, `cpufreq`) | ✓ | – | – | 1 |
| `kernel.maxfiles`, `kernel.maxproc`, `kernel.openfiles` | OS files (`/proc/sys/fs`, `/proc/sys/kernel`); platform call (`sysctl`) | ✓ | – | 3 | 1 |
| `vm.memory.size[<mode>]` | OS files (`/proc/meminfo`, every mode Zabbix lists for Linux); platform calls (`GlobalMemoryStatusEx`; `sysctl hw.memsize` and `host_statistics64`) | ✓ | 3 | 3 | 1 |
| `vm.vmemory.size[<type>]` | platform call (`GlobalMemoryStatusEx`) | – | 3 | – | 3 |
| `proc.num`, `proc.mem` | OS files (`/proc/<pid>`: name, user, state, command line, every memory type); `Process` elsewhere (name; `vsize` and `rss`); WMI (user and command line on Windows) | ✓ | name, 4 | name | 1 |
| `proc.get` | OS files (`/proc/<pid>`); `Process` and WMI on Windows. Zabbix's agent has no macOS `proc.get`, so there is no JSON to match there | ✓ | 4 | – | 1 |
| `proc.cpu.util` | a collector over `Process` CPU times | 2 | 2 | 2 | 2 |
| `proc_info[process,<attribute>,<type>]` | `Process` (`ktime`, `utime`); platform calls (`vmsize`, `wkset`, `pf`, `io_*`, `gdiobj`, `userobj`: `GetProcessIoCounters`, `GetProcessMemoryInfo`, `GetGuiResources`) | – | ✓, 3 | – | 1 |
| `vfs.fs.discovery`, `vfs.fs.get`, `vfs.fs.size[fs,<mode>]` | `DriveInfo`: Zabbix computes a file system's figures from its total, free and caller-available blocks, the three `DriveInfo` reports; OS files (`/proc/mounts`) for mount options | ✓ | ✓ | ✓ | 1 |
| `vfs.fs.inode[fs,<mode>]` (and `vfs.fs.get`'s inodes) | platform call (`statvfs`) | 3 | – | 3 | 3 |
| `vfs.dev.discovery`, `vfs.dev.read`, `vfs.dev.write` (`sectors`, `operations`) | OS files (`/sys/class/block`, `/proc/diskstats`) | ✓ | – | – | 1 |
| `vfs.dev.read`, `vfs.dev.write` (`sps`, `ops`) | a collector over OS files (`/proc/diskstats`) | 2 | – | – | 2 |
| `vfs.file.exists`, `vfs.file.size`, `vfs.file.time` | `File`, `FileInfo`; platform calls (`stat`: the change time, sockets, devices and pipes on Unix; the change time on Windows) | ✓, 3 | ✓, 3 | ✓, 3 | 1 |
| `vfs.file.contents`, `vfs.file.regexp`, `vfs.file.regmatch` | `File`, `Regex`, `Encoding` | ✓ | ✓ | ✓ | 1 |
| `vfs.file.md5sum`, `vfs.file.cksum[file,<mode>]` | `MD5`, `SHA256`; the POSIX `cksum` CRC, written in the SDK (`System.IO.Hashing.Crc32` is zlib's CRC, a different sum) | ✓ | ✓ | ✓ | 1 |
| `vfs.file.permissions` | `File.GetUnixFileMode` | ✓ | – | ✓ | 1 |
| `vfs.file.owner`, `vfs.file.get` | `FileSystemAclExtensions` on Windows; platform call (`stat`) on Unix | 3 | 3 | 3 | 3 |
| `vfs.dir.count` | `FileSystemEnumerable<T>`, `EnumerationOptions`, `Regex` | ✓ | ✓ | ✓ | 1 |
| `vfs.dir.get`, `vfs.dir.size` | as `vfs.dir.count`, with each entry's owner, and its size as `stat` gives it (a directory's own, the blocks for `disk`) | 3 | 3 | 3 | 3 |
| `net.if.discovery`, `net.if.list`, `net.if.in`, `net.if.out`, `net.if.total` | OS files (`/proc/net/dev`, every mode); `NetworkInterface`, `IPInterfaceStatistics` elsewhere (bytes, packets, errors, drops; macOS has no drops) | ✓ | ✓ | ✓ | 1 |
| `net.if.collisions` | OS files (`/sys/class/net/<if>/statistics/collisions`) | ✓ | – | – | 1 |
| `net.tcp.listen`, `net.udp.listen`, `net.tcp.socket.count`, `net.udp.socket.count` | `IPGlobalProperties` (a UDP socket by its local end only, so `net.udp.socket.count` counts unconnected ones) | ✓ | ✓ | listen | 1 |
| `net.tcp.port`, `net.tcp.service`, `net.tcp.service.perf` | `Socket` with Zabbix's greeting checks (`ssh`, `smtp`, `ftp`, `pop`, `imap`, `nntp`, `http`, `telnet`, `tcp`), `SslStream` (`https`); `ldap` is not bundled: no LDAP client is in the framework | ✓ | ✓ | ✓ | 1 |
| `net.udp.service[ntp]`, `net.udp.service.perf[ntp]` | `UdpClient`, an NTP request | ✓ | ✓ | ✓ | 1 |
| `net.dns`, `net.dns.perf`, `net.dns.record` | a DNS message client (RFC 1035 over `Socket`), written in the SDK and asking as `res_nsend` asks: the system resolver has no named server and no other record type, and two ways of answering one key would differ | 3 | 3 | 3 | 3 |
| `web.page.get`, `web.page.perf`, `web.page.regexp` | `HttpClient` (no redirects followed, as Zabbix's agent) | ✓ | ✓ | ✓ | 1 |
| `web.certificate.get` | `SslStream`, `X509Certificate2`, `X509Chain` | ✓ | ✓ | ✓ | 1 |
| `zabbix.stats[<ip>,<port>]`, its `queue` form | the SDK's wire (`zabbix.stats` request), to 127.0.0.1:10051 by default, as Zabbix's agent | ✓ | ✓ | ✓ | 1 |
| `registry.data`, `registry.get` | `Microsoft.Win32.Registry`, with the Windows package's keys | – | 4 | – | 4 |
| `perf_counter*`, `perf_instance*.discovery`, `service.*`, `services`, `wmi.*` | the Windows package (§2) | – | 4 | – | 4 |
| `log`, `logrt`, `log.count`, `logrt.count`, `eventlog`, `eventlog.count` | `FileStream`, `Regex`; `EventLogReader` (the Windows package): active checks that send several values per check and resume from `lastlogsize` and `mtime`, which the active agent already round-trips | 5 | 5 | 5 | 5 |
| `system.run` | not bundled (§3) | | | | |
| `system.users.num`, `system.hw.chassis`, `system.hw.devices`, `system.sw.packages*`, `system.stat`, `sensor`, `modbus.get` | not bundled: no framework source; candidates for plugins | | | | |

### 5. Collectors

`system.cpu.util`, `proc.cpu.util` and the per-second types of `vfs.dev.read` and `vfs.dev.write` report over one, five
or fifteen minutes, so they need samples taken before they are asked. One collector samples them every second and keeps
fifteen minutes of each thing asked about, as Zabbix's collector does, and its timer runs only while something is asked
about: a series starts at its first request and is dropped when it goes unrequested (a day for a processor or a process
query, three hours for a block device, as `PROCSTAT_MAX_INACTIVITY_PERIOD` and `DISKDEVICE_TTL` have it). The agent's
container owns the collector and disposes of it with the agent. A table built without a container has no owner, so the
timer holds its collector weakly, and it stops once nothing holds the table.

A new series' first request waits for its second sample within the check's deadline, as Zabbix's `proc.cpu.util` waits
for its collector's data (`system.cpu.util` and `vfs.dev` would otherwise answer an average since boot, or zero, on a
collector that has only just started). Until a series holds a full window, its key answers from the samples it has, as
Zabbix's agent does.

### 6. Platform calls

The bundled items may call a short list of documented OS functions through source-generated `LibraryImport` (Native
AOT-compatible, no runtime marshalling stubs), each behind `OperatingSystem.Is…()` and `[SupportedOSPlatform]`:

| Platform | Functions |
| --- | --- |
| Linux and macOS (libc) | `statvfs`; `statx` on Linux, whose layout is the same on every architecture where `stat`'s is not; `stat` and `lstat` on macOS; `getpwuid_r` and `getgrgid_r` for a file's owner |
| macOS | `getloadavg`, `sysctlbyname` (`kern.boottime`, `hw.memsize`, `hw.activecpu`, `hw.ncpu`, `vm.swapusage`, `kern.maxfiles`, `kern.maxproc`), `mach_host_self`, `host_statistics64`, `host_processor_info`, `vm_deallocate` |
| Windows | `GlobalMemoryStatusEx`, `K32GetPerformanceInfo` (`vm.memory.size[cached]`), `GetSystemTimes`, `OpenProcess`, `GetProcessIoCounters`, `K32GetProcessMemoryInfo`, `GetGuiResources`, `CreateFileW`, `GetFileInformationByHandleEx` (a file's change time), `GetFileInformationByHandle` (a hard link's identity), `GetCompressedFileSizeW`, `GetVolumePathNameW`, `GetDiskFreeSpaceW` (`vfs.dir.size`) |

A function not on the list is a change to this ADR.

### 7. Every surface answers them

- **The SDK.** The active and passive agents answer the bundled set, composed through DI or `ZabbixRunner` alike.
  `ZabbixItemHandlers.BuiltIn` returns it, so a custom handler or provider answers a bundled key through
  `AnswerAsync`, or calls a family provider directly.
- **The PowerShell sessions.** `Start-ZbxActiveAgent` and `Start-ZbxPassiveAgent` take `-KeyRule` (Zabbix's lines, in
  order) and `-NoBundledItems`; their tables are composed by the SDK, so they answer the bundled keys with no code of
  their own.
  `Test-ZbxAgentItem` answers them without a session.
- **Scriptblock items.** A scriptblock handler reads a bundled key with
  `Test-ZbxAgentItem -Key 'vfs.fs.size[/,pfree]' -Raw`, and a session's own table with `-Session`, so a custom key can
  be built from bundled ones.

The waves are the order of work: 1, everything the framework and Linux's files answer; 2, the collectors; 3, the
platform calls and the DNS client; 4, the Windows package; 5, the log keys.

## Rejected

- **Running the OS's tools** (`df`, `ps`, `uptime`, `sysctl`, `wmic`, PowerShell cmdlets). This starts a process per
  check, parses output that changes with the locale and the version, and needs the tool on the host.
- **Approximating a key from a portable API.** For example, `vm.memory.size[available]` from the GC's memory figures.
  The value would differ from Zabbix's agent, and a template's trigger would fire on the difference.
- **A separate package for the portable keys.** They add no dependency, and every agent should answer them.
- **The bundled keys as an ordinary provider.** Two providers that list one key fail the start (ADR 0008), so an
  application's provider of `vfs.fs.size` would conflict with the bundled set. The lowest tier lets it take the name.
- **Microsoft.Diagnostics.NETCore.Client for counters of other .NET processes.** It is not a host measure and not a
  Zabbix key. An application can add it as a provider.

## Consequences

- **Packages.** `Community.Zabbix.Agent` gains the family providers, the collectors and the platform calls, and no
  dependency. `Community.Zabbix.Agent.Windows` joins [ADR 0002](0002-package-ids.md)'s list and
  `DesignConformanceTests.Dependencies`.
- **Options.** `KeyRules` and `BundledItems` join both agents' options and the configuration guide.
- **Tests.**
  - Each key's parameters, modes, texts and JSON shapes. The Linux readers take a root path, so the tests point them
    at a fixture tree of `/proc`, `/sys` and `/etc`, and hold them on every system the tests run on; the file, network
    and web keys run against real files and loopback servers.
  - The precedence: a handler and a provider over a bundled key. The key rules against Zabbix's own golden cases
    (`tests/libs/zbxsysinfo/zbx_check_key_access_rules.yaml`).
  - The keys' Windows and macOS paths are tested where those systems run the tests: the Windows and macOS legs of CI's
    `build` job. A lab run that asks the SDK's agent and Zabbix agent 2 for the same keys (`tests/lab`) belongs with
    those legs.
- **Native AOT.** The AOT probe composes an agent, so the bundled set is trim- and AOT-safe: regular expressions from
  key parameters run on the interpreter, and the platform calls are source-generated.
- **Documentation.** The bundled items reference (`docs/guides/bundled-items.md`) lists each family's keys, their
  platforms and their differences from Zabbix's agent. The items guide's row "The four keys every agent answers" becomes the bundled set, and the
  agent module's about topic gains the new parameters.

## Not decided here

- **Keys of the SDK's own.** For example, an agent process's runtime counters under a `dotnet.*` prefix: not Zabbix
  keys, so no template asks for them.
- **Command items** (`system.run`, `UserParameter`), as in ADR 0008.

## The proposal's open questions, answered

1. **On by default:** everything bundled, as Zabbix's agents allow everything but `system.run`.
2. **Platform calls:** the functions §6 lists.
3. **The Windows package:** with 1.0.
4. **The names** are the proposed ones, with one change: `AllowKey` and `DenyKey` are one ordered list, `KeyRules`
   (`-KeyRule` in PowerShell), because Zabbix's order decides between them and two lists would lose it.

## Amendment (2026-11-03): what a template gets, the reasons' wording, and corrections

- **Templates.** Section 1 says Zabbix's templates work unchanged. That holds for what a template asks of the keys the
  SDK answers. A template that also uses a key this decision leaves out (the Windows package's, the log keys) gets that
  key's not-supported reason for it.
- **The reasons are the SDK's own words.** A key answers Zabbix's parameters, modes, units and JSON shapes, which are
  facts. The text of a not-supported reason is not one: the project never reproduces Zabbix's messages (CONTRIBUTING,
  "Design standard"; the [decisions register](../design/decisions.md#zabbixs-trademark-and-licences)), so "texts" in the
  test plan above means the SDK's own.
- **Corrections to section 4's table.** `system.sw.os` is not answered on macOS; `net.if.list` is answered on Windows
  only; `kernel.openfiles` is not answered on macOS. [The bundled items guide](../guides/bundled-items.md) has them right.
- **An addition to section 6.** On macOS the bundled items also read the C library's `mach_task_self_`, which is a
  variable rather than a function, through `NativeLibrary.GetExport`, to release what `host_processor_info` allocates.

## Amendment (2026-11-04): where the bundled keys sit

[ADR 0010](0010-agent-proxy-client-classes.md) §4 replaces the table's lowest tier in section 3: when an agent starts, it
adds the four keys every agent answers and, unless `BundledItems` is off, the bundled keys, under every name left free.
They still answer below every other key. `ZabbixItemHandlers` is `ZabbixItemDictionary`, an agent's `Items`.
