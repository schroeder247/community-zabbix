<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Bundled items

Every active and passive agent answers Zabbix's own item keys out of the box: the host's memory, processors, file systems,
processes, files, network interfaces and services, web pages, and certificates. The keys are in the `Community.Zabbix.Agent`
package.

The keys take Zabbix's parameters and answer with Zabbix's values and units, so an item or a template that asks for them works
against an SDK agent unchanged. A template that also asks for a key under
[Keys the bundled set does not answer](#keys-the-bundled-set-does-not-answer) gets that key's reason. A reason is the SDK's own
wording.

A key, parameter, or mode a platform can't answer the way Zabbix's agent does isn't supported, with the reason. It's never
approximated. The tables list each key's platforms and every difference from Zabbix's agent.

## Read them with their types

`ZabbixItemKeys` declares each of Zabbix's keys with its type, grouped by what it reads, so a read needs no string and no parsing:

```csharp
double load = await wire.GetAgentItemAsync("web-01", ZabbixItemKeys.Cpu.Load());
long free = await wire.GetAgentItemAsync("web-01", ZabbixItemKeys.Disk.FreeBytes("/"));

double percent = await zabbix.Hosts["web-01"].GetLastAsync(ZabbixItemKeys.Disk.FreePercent("/"));
bool up = await zabbix.Hosts["web-01"].GetLastAsync(ZabbixItemKeys.Agent.Ping);
```

| Group | What it reads | For example |
| --- | --- | --- |
| `Agent` | The agent itself | `Ping`, `Version`, `Hostname` |
| `Host` | The host | `Uptime`, `LocalTime`, `OsVersion` |
| `Cpu` | Processors | `Load()`, `Count` |
| `Memory`, `Swap` | Memory and swap | `FreeBytes`, `Memory.AvailablePercent` |
| `Disk` | File systems and block devices | `FreeBytes(path)`, `FreePercent(path)` |
| `File` | Files and folders | `Exists(path)`, `LineCount(path)` |
| `Process` | Processes | `Count()` |
| `Kernel` | Kernel limits | `MaxFiles`, `OpenFiles` |
| `Network` | Interfaces and connections | `BytesReceived(name)` |
| `Web` | Web pages and certificates | `Page(host)`, `Certificate(hostname)` |

`vfs.fs.size[/,free]` is a whole number and `vfs.fs.size[/,pfree]` a decimal, so the catalogue has an entry for each. It's in
`Community.Zabbix.Abstractions`, so a read needs no agent package.

## On, off, and restricted

The bundled keys sit below every other key an agent answers. At start they fill the names left free, so a handler, a provider's
key, or a plugin's key of the same name answers instead. See [The rules every key follows](items.md#the-rules-every-key-follows).

| To | C# | Configuration | PowerShell |
| --- | --- | --- | --- |
| Restrict the keys | `agent.Options.KeyRules.Add("DenyKey=vfs.file.contents[*]")` | `"KeyRules": [ "AllowKey=agent.*", "DenyKey=*" ]` | `-KeyRule 'AllowKey=agent.*', 'DenyKey=*'` |
| Leave the bundled keys out | `agent.Options.BundledItems = false` | `"BundledItems": false` | `-NoBundledItems` |

`KeyRules` are Zabbix's `AllowKey` and `DenyKey` lines. They're tried in order, and the first that matches decides. A key no rule
matches is allowed. A pattern is one item key in which `*` matches any characters:

| Pattern | Matches | Does not match |
| --- | --- | --- |
| `*` | every key | |
| `vfs.file.*` | `vfs.file.size`, without brackets only | `vfs.file.size[/etc/passwd]` |
| `vfs.file.*[*]` | `vfs.file.size[/etc/passwd]`, `vfs.file.contents[]` | `vfs.file.size` |
| `vfs.file.contents[/etc/passwd,*]` | `vfs.file.contents[/etc/passwd]`, `vfs.file.contents[/etc/passwd,utf8]` | `vfs.file.contents[/etc/hosts]` |
| `vfs.file.contents[*passwd*]` | `vfs.file.contents[/etc/passwd]` | `vfs.file.contents[/etc/passwd,utf8]` |

A denied key is answered as one nobody answers: "This agent does not answer the key." As in Zabbix's agents, `system.run` is
denied unless a rule allows it, and rules that allow every key fail the agent's start.

```csharp
using System.Net;

await using ZabbixPassiveAgent agent = ZabbixPassiveAgent.CreateBuilder("web-01")   // the host
    .AllowPeer(IPAddress.Parse("10.0.0.10"))                                       // the Zabbix server
    .Configure(o =>
    {
        o.KeyRules.Add("DenyKey=vfs.file.contents[*]");                            // Zabbix's lines, tried in order
        o.KeyRules.Add("DenyKey=web.*[*]");
    })
    .Build();
await agent.StartAsync();
```

## Read a bundled key from code

Read it through any agent's table (`ZabbixItemDictionary.BuiltIn(hostname)` and `AnswerAsync`), or from its family's provider,
which a handler or a provider of your own can call:

```csharp
using Community.Zabbix.Items;

var system = new ZabbixSystemItems();
ZabbixAgentAnswer available = await system.GetValueAsync(ZabbixItemKey.Parse("vm.memory.size[pavailable]", null), CancellationToken.None);
```

In PowerShell, `Test-ZbxAgentItem` answers a bundled key without a session, and a scriptblock handler builds a key of its own from
them:

```powershell
Register-ZbxPassiveAgentHandler -Name 'web-agent' -Key 'app.disk.alarm' -ScriptBlock {
    if ([double](Test-ZbxAgentItem -Key 'vfs.fs.size[/,pused]' -Raw) -gt 90) { 1 } else { 0 }
}
```

## The families

Each family is a public provider in the `Community.Zabbix.Items` namespace. In the tables, ✓ means every parameter and mode
Zabbix's agent answers on that platform, and a note names what's left out.

### Agent (`ZabbixItemDictionary.BuiltIn`)

| Key | Linux | Windows | macOS | Notes |
| --- | --- | --- | --- | --- |
| `agent.ping`, `agent.version`, `agent.hostname`, `system.uname` | ✓ | ✓ | ✓ | the four every agent answers, also without the bundled set |
| `agent.variant` | ✓ | ✓ | ✓ | `2`, as Zabbix agent 2 |
| `agent.hostmetadata` | ✓ | ✓ | ✓ | the active agent's `HostMetadata`; empty for a passive agent |

### System (`ZabbixSystemItems`)

| Key | Linux | Windows | macOS | Notes |
| --- | --- | --- | --- | --- |
| `system.hostname[<type>,<transform>]` | ✓ | ✓ | ✓ | `netbios` on Windows only, as Zabbix |
| `system.localtime[<type>]` | ✓ | ✓ | ✓ | the agent's clock |
| `system.uptime`, `system.boottime` | ✓ | ✓ | ✓ | Linux `/proc/uptime` and `/proc/stat`; Windows the tick count, which counts sleep too; macOS `kern.boottime` |
| `system.cpu.num[<type>]` | ✓ | `online` | ✓ | on Windows, the processors this process may use |
| `system.cpu.discovery` | ✓ | ✓ | ✓ | outside Linux, every processor is online |
| `system.cpu.load[<cpu>,<mode>]` | ✓ | | ✓ | |
| `system.cpu.util[<cpu>,<type>,<mode>]` | ✓ | `all`, `system` | `user`, `nice`, `system`, `idle` | [sampled](#keys-sampled-over-time); Zabbix's Windows agent reports each processor too, from a performance counter; its macOS agent has no such key |
| `system.cpu.switches`, `system.cpu.intr` | ✓ | | | |
| `system.sw.arch` | ✓ | ✓ | ✓ | from the runtime's OS architecture, in `uname`'s names (`x86_64`, `aarch64`); Windows `x86` and `x64` only, as Zabbix |
| `system.sw.os[<info>]`, `system.sw.os.get` | ✓ | ✓ | | Linux `/proc/version`, `/etc/os-release`; Windows the registry |
| `system.swap.size[<device>,<type>]` | ✓ | `total`, `free`, `pfree`, `used` | ✓ | Windows has commit and physical memory, so swap is the difference, as Zabbix works it out; macOS `vm.swapusage`, where Zabbix's agent has no such key |
| `system.swap.in[<device>,<type>]`, `system.swap.out[…]` | ✓ | | | |
| `system.hw.cpu[<cpu>,<info>]` | ✓ | | | |
| `system.hw.macaddr[<interface>,<format>]` | ✓ | ✓ | ✓ | the .NET interface list |
| `kernel.maxfiles`, `kernel.maxproc` | ✓ | | ✓ | |
| `kernel.openfiles` | ✓ | | | |
| `vm.memory.size[<mode>]` | ✓ | `total`, `free`, `used`, `pused`, `available`, `pavailable`, `cached` | `total`, `active`, `inactive`, `wired`, `free`, `used`, `pused`, `available`, `pavailable` | each system's modes, as Zabbix; `shared` is Linux 2.4's only |
| `vm.vmemory.size[<type>]` | | ✓ | | the commit limit and what is left of it |

### Processes (`ZabbixProcessItems`)

| Key | Linux | Windows | macOS | Notes |
| --- | --- | --- | --- | --- |
| `proc.num[<name>,<user>,<state>,<cmdline>]` | ✓ | name | name | Windows matches the image name (`explorer.exe`) |
| `proc.mem[<name>,<user>,<mode>,<cmdline>,<memtype>]` | ✓ | name, `vsize`, `rss` | name, `vsize`, `rss` | |
| `proc.cpu.util[<name>,<user>,<type>,<cmdline>,<mode>]` | ✓ | name | name | [sampled](#keys-sampled-over-time); a percentage of one processor, to one decimal |
| `proc.get[<name>,<user>,<cmdline>,<mode>]` | ✓ | | | the `process`, `thread` and `summary` modes; a value that cannot be read is `-1`; CPU times assume `USER_HZ` is 100 |
| `proc_info[process,<attribute>,<type>]` | | ✓ | | every attribute; a process this account may not open is left out, as Zabbix |

On macOS, another account's processes are counted but their memory and times read as zero without root.

### File systems and block devices (`ZabbixFileSystemItems`)

| Key | Linux | Windows | macOS | Notes |
| --- | --- | --- | --- | --- |
| `vfs.fs.discovery` | ✓ | ✓ | ✓ | Linux `/proc/mounts` with `{#FSOPTIONS}`; Windows with `{#FSLABEL}` and `{#FSDRIVETYPE}` |
| `vfs.fs.size[fs,<mode>]` | ✓ | ✓ | ✓ | `free` leaves out the reserved blocks; the percentages are of used + free |
| `vfs.fs.inode[fs,<mode>]` | ✓ | | ✓ | not on 32-bit Linux |
| `vfs.fs.get` | ✓ | ✓ | ✓ | `inodes` on Linux and macOS, except 32-bit Linux; Zabbix's macOS agent leaves them out |
| `vfs.dev.discovery` | ✓ | | | `/sys/class/block` |
| `vfs.dev.read[<device>,<type>,<mode>]`, `vfs.dev.write[…]` | ✓ | | | `sps` and `ops` are [sampled](#keys-sampled-over-time) |

### Files and directories (`ZabbixFileItems`)

| Key | Linux | Windows | macOS | Notes |
| --- | --- | --- | --- | --- |
| `vfs.file.exists[file,<types incl>,<types excl>]` | ✓ | ✓ | ✓ | every type; a link is both itself and what it points at; Windows has only `file`, `dir` and `sym` |
| `vfs.file.size[file,<mode>]` | ✓ | ✓ | ✓ | `lines` counts newlines |
| `vfs.file.time[file,<mode>]` | ✓ | ✓ | ✓ | |
| `vfs.file.owner[file,<owner type>,<result type>]` | ✓ | `user` | ✓ | an account without a name answers its id; a Windows id is a SID |
| `vfs.file.get[file]` | ✓ | ✓ | ✓ | Zabbix's JSON; a link is described as itself; Windows has `SID`, no group, permissions or numeric ids |
| `vfs.file.contents[file,<encoding>]` | ✓ | ✓ | ✓ | at most 16 MB; the byte order mark and trailing newlines left out |
| `vfs.file.regexp[file,regexp,<encoding>,<start line>,<end line>,<output>]` | ✓ | ✓ | ✓ | .NET regular expressions; `\0` to `\9` in the output |
| `vfs.file.regmatch[file,regexp,<encoding>,<start line>,<end line>]` | ✓ | ✓ | ✓ | |
| `vfs.file.md5sum[file]`, `vfs.file.cksum[file,<mode>]` | ✓ | ✓ | ✓ | `crc32` is the POSIX `cksum` |
| `vfs.file.permissions[file]` | ✓ | | ✓ | not on Windows, as Zabbix |
| `vfs.dir.count[dir,…]`, `vfs.dir.get[dir,…]` | ✓ | ✓ | ✓ | every Zabbix parameter; links are not followed; `get` writes each entry as `vfs.file.get` does |
| `vfs.dir.size[dir,<regex incl>,<regex excl>,<mode>,<max depth>,<regex excl dir>]` | ✓ | ✓ | ✓ | a file with several names counts once. Linux and macOS count the directories' own records too; Windows counts its files only, as compressed, and skips links, as Zabbix |

### Network (`ZabbixNetworkItems`)

| Key | Linux | Windows | macOS | Notes |
| --- | --- | --- | --- | --- |
| `net.if.discovery` | ✓ | ✓ | ✓ | |
| `net.if.list` | | ✓ | | |
| `net.if.in[if,<mode>]`, `net.if.out[…]`, `net.if.total[…]` | ✓ | `bytes`, `packets`, `errors`, `dropped` | `bytes`, `packets`, `errors` | Linux `/proc/net/dev`, every mode; Windows names an interface by name, description, address or `{GUID}` |
| `net.if.collisions[if]` | ✓ | | | |
| `net.tcp.listen[port]`, `net.udp.listen[port]` | ✓ | ✓ | ✓ | |
| `net.tcp.socket.count[<laddr>,<lport>,<raddr>,<rport>,<state>]` | ✓ | ✓ | | |
| `net.udp.socket.count[<laddr>,<lport>]` | ✓ | ✓ | | unconnected sockets only: the framework lists a UDP socket by its local end |
| `net.tcp.port[<ip>,port]` | ✓ | ✓ | ✓ | |
| `net.tcp.service[service,<ip>,<port>]`, `net.tcp.service.perf[…]` | ✓ | ✓ | ✓ | `ssh`, `smtp`, `ftp`, `http`, `pop`, `nntp`, `imap`, `tcp`, `https`, `telnet`; not `ldap` |
| `net.udp.service[ntp,<ip>,<port>]`, `net.udp.service.perf[…]` | ✓ | ✓ | ✓ | |
| `net.dns[<ip>,name,<type>,<timeout>,<count>,<protocol>]`, `net.dns.perf[…]`, `net.dns.record[…]` | ✓ | ✓ | ✓ | asks the server named, or the system's: `/etc/resolv.conf`, on Windows its interfaces'. Every record type; `WKS` ports are numbers, and its protocol a name only for `icmp`, `tcp` and `udp`. Unlike Zabbix's agent, Windows honours `<ip>` and has `WKS` |
| `zabbix.stats[<ip>,<port>]`, `zabbix.stats[<ip>,<port>,queue,<from>,<to>]` | ✓ | ✓ | ✓ | through the SDK's `ZabbixWireClient` |

### Web (`ZabbixWebItems`)

| Key | Linux | Windows | macOS | Notes |
| --- | --- | --- | --- | --- |
| `web.page.get[host,<path>,<port>]`, `web.page.perf[…]` | ✓ | ✓ | ✓ | redirects not followed, the server's certificate not checked, as Zabbix |
| `web.page.regexp[host,<path>,<port>,regexp,<length>,<output>]` | ✓ | ✓ | ✓ | |
| `web.certificate.get[hostname,<port>,<address>]` | ✓ | ✓ | ✓ | agent 2's key and JSON; the chain is checked against the host's trust store |

## Keys sampled over time

`system.cpu.util`, `proc.cpu.util`, and the `sps` and `ops` types of `vfs.dev.read` and `vfs.dev.write` report an average over
one, five, or fifteen minutes (`avg1`, `avg5`, `avg15`), so they need samples from before they're asked. As in Zabbix's agent,
each thing they measure is sampled once a second and its last fifteen minutes are kept. Unlike Zabbix's agent, the sampling runs
only while a key asks:

- **It starts at the first request.** That request waits for a second sample, about a second, within the check's deadline, and
  answers from it. Until the window is full, a key answers from the samples it has.
- **It stops when nothing asks.** A processor or a process query is dropped after a day without a request, and a block device
  after three hours, as Zabbix's agent drops them. The next request starts again from nothing. At most 1,024 process queries and
  1,024 block devices are followed.
- **It ends with the agent,** whose container owns the sampling. `Test-ZbxAgentItem` without `-Session` samples only for the
  length of the command, so its `avg1` covers about a second. Give it a running session to read that session's history.

A provider built on its own (`new ZabbixSystemItems()`, `AddItems<ZabbixProcessItems>()`) samples for itself, and stops when it's
disposed.

## Keys the bundled set does not answer

These Zabbix keys are answered as "This agent does not answer the key." unless a handler, a provider, or a plugin answers them:

- **Windows' own sources:** `registry.data`, `registry.get`, `perf_counter`, `perf_counter_en`,
  `perf_instance.discovery`, `perf_instance_en.discovery`, `service.discovery`, `service.info`, `services`, `wmi.get`,
  `wmi.getall`, `eventlog`, `eventlog.count`.
- **Log files:** `log`, `logrt`, `log.count`, `logrt.count`.
- **No framework source:** `system.users.num`, `system.hw.chassis`, `system.hw.devices`, `system.sw.packages`,
  `system.sw.packages.get`, `system.stat`, `sensor`, `modbus.get`.
- **Commands:** `system.run`.

## See also

- [Items](items.md): handlers, providers, and the rules an agent's table follows.
- [Plugins](plugins.md): item keys loaded from a folder.
- [Configuration](configuration.md): `KeyRules` and `BundledItems` in a document.
- [ADR 0009](../adr/0009-bundled-items.md): the decision, and how each key is measured.
