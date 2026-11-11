// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;

namespace Community.Zabbix;

/// <summary>
/// The item keys Zabbix's own agents answer, each paired with the type of its value, so a caller names what it wants instead of
/// writing a key's text and parsing the answer.
/// </summary>
/// <remarks>
/// <para>
/// A key with nothing to choose is a property, and a key that takes parameters is a method. An optional argument left at its
/// default stays out of the key, unless a later argument needs its place. Where a key's value changes type with a mode, each mode
/// has a member of its own, and that member's key always names the mode.
/// </para>
/// <para>
/// The keys are written as Zabbix's own templates write them, because a read through the API finds an item by its key. A read
/// matches a key's parameters however the item on the server quotes them. If a template writes the parameters differently, use
/// <see cref="ZabbixItemKey{T}.With"/>.
/// </para>
/// <para>
/// A method throws <see cref="ArgumentNullException"/> or <see cref="ArgumentException"/> for a required text that is
/// <see langword="null"/> or empty, <see cref="ArgumentOutOfRangeException"/> for a number no key accepts or a value an enumeration
/// doesn't define, and <see cref="FormatException"/> for a text a key can't hold, such as one that needs quotes and ends with a
/// backslash.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// double load = await wire.GetAgentItemAsync("web-01", ZabbixItemKeys.Cpu.Load());
/// long free   = await wire.GetAgentItemAsync("web-01", ZabbixItemKeys.Disk.FreeBytes("/"));
/// string name = await wire.GetAgentItemAsync("web-01", ZabbixItemKeys.Host.Uname);
/// </code>
/// </example>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The groups are the catalogue's table of contents: ZabbixItemKeys.Disk.FreeBytes reads as one name, and each is a static class nobody constructs.")]
public static class ZabbixItemKeys
{
    /// <summary>How far back an average over an agent's own samples reaches.</summary>
    public enum SampleWindow
    {
        /// <summary>The last minute.</summary>
        OneMinute,

        /// <summary>The last five minutes.</summary>
        FiveMinutes,

        /// <summary>The last fifteen minutes.</summary>
        FifteenMinutes,
    }

    /// <summary>What an agent says about itself.</summary>
    public static class Agent
    {
        /// <summary>Whether the agent is up, which an agent that answers at all reads as <see langword="true"/>: <c>agent.ping</c>.</summary>
        public static ZabbixItemKey<bool> Ping { get; } = Item<bool>("agent.ping");

        /// <summary>The agent's version number, as text such as <c>7.4.0</c>: <c>agent.version</c>.</summary>
        public static ZabbixItemKey<string> Version { get; } = Item<string>("agent.version");

        /// <summary>The name the agent was configured to report, not necessarily the machine's: <c>agent.hostname</c>.</summary>
        public static ZabbixItemKey<string> Hostname { get; } = Item<string>("agent.hostname");

        /// <summary>Which of Zabbix's two agents this one is or behaves as, 1 or 2: <c>agent.variant</c>.</summary>
        public static ZabbixItemKey<long> Variant { get; } = Item<long>("agent.variant");

        /// <summary>The metadata an active agent registers with, empty for a passive agent: <c>agent.hostmetadata</c>.</summary>
        public static ZabbixItemKey<string> HostMetadata { get; } = Item<string>("agent.hostmetadata");
    }

    /// <summary>The machine as a whole: its names, how long it has run, its clock, its architecture and its operating system.</summary>
    public static class Host
    {
        /// <summary>Which of a machine's names to read.</summary>
        public enum HostnameKind
        {
            /// <summary>Whichever the key answers when not told: the NetBIOS name on Windows, the host name elsewhere.</summary>
            Default,

            /// <summary>The name the system calls itself.</summary>
            HostName,

            /// <summary>The name up to its first dot.</summary>
            ShortHostName,

            /// <summary>The name with its domain, from a name lookup.</summary>
            FullyQualifiedName,

            /// <summary>The NetBIOS name, which only Windows has.</summary>
            NetBiosName,
        }

        /// <summary>One of the machine's names: <c>system.hostname[type,transform]</c>.</summary>
        /// <param name="kind">Which of the machine's names to read.</param>
        /// <param name="lowercase">Whether the agent writes the name in lowercase.</param>
        /// <returns>The item for that name.</returns>
        /// <remarks>Only Windows answers <see cref="HostnameKind.NetBiosName"/>.</remarks>
        public static ZabbixItemKey<string> Hostname(HostnameKind kind = HostnameKind.Default, bool lowercase = false)
            => Item<string>("system.hostname", Text(kind), lowercase ? "lower" : string.Empty);

        /// <summary>One line describing the operating system and the machine: <c>system.uname</c>.</summary>
        public static ZabbixItemKey<string> Uname { get; } = Item<string>("system.uname");

        /// <summary>How long ago the machine started: <c>system.uptime</c>.</summary>
        public static ZabbixItemKey<TimeSpan> Uptime { get; } = Item<TimeSpan>("system.uptime");

        /// <summary>When the machine started: <c>system.boottime</c>.</summary>
        public static ZabbixItemKey<DateTimeOffset> BootTime { get; } = Item<DateTimeOffset>("system.boottime");

        /// <summary>The time on the machine's clock, read as seconds since the epoch and so at offset zero: <c>system.localtime</c>.</summary>
        public static ZabbixItemKey<DateTimeOffset> LocalTime { get; } = Item<DateTimeOffset>("system.localtime");

        /// <summary>
        /// The time on the machine's clock in its own time zone, as text holding the date, the time and the offset, separated by
        /// commas: <c>system.localtime[local]</c>.
        /// </summary>
        public static ZabbixItemKey<string> LocalTimeText { get; } = Item<string>("system.localtime", "local");

        /// <summary>The name of the processor architecture, such as <c>x86_64</c>: <c>system.sw.arch</c>.</summary>
        public static ZabbixItemKey<string> Architecture { get; } = Item<string>("system.sw.arch");

        /// <summary>The operating system's long version text: <c>system.sw.os</c>.</summary>
        /// <remarks>Linux and Windows only.</remarks>
        public static ZabbixItemKey<string> OsVersion { get; } = Item<string>("system.sw.os");

        /// <summary>The operating system's version in brief: <c>system.sw.os[short]</c>.</summary>
        /// <remarks>Linux and Windows only.</remarks>
        public static ZabbixItemKey<string> OsShortVersion { get; } = Item<string>("system.sw.os", "short");

        /// <summary>The operating system's product name: <c>system.sw.os[name]</c>.</summary>
        /// <remarks>Linux and Windows only.</remarks>
        public static ZabbixItemKey<string> OsName { get; } = Item<string>("system.sw.os", "name");

        /// <summary>The operating system's name, architecture and version parts, as JSON: <c>system.sw.os.get</c>.</summary>
        /// <remarks>Linux and Windows only.</remarks>
        public static ZabbixItemKey<string> OsDetails { get; } = Item<string>("system.sw.os.get");

        private static string Text(HostnameKind kind) => kind switch
        {
            HostnameKind.Default => string.Empty,
            HostnameKind.HostName => "host",
            HostnameKind.ShortHostName => "shorthost",
            HostnameKind.FullyQualifiedName => "fqdn",
            HostnameKind.NetBiosName => "netbios",
            _ => throw Undefined(kind),
        };
    }

    /// <summary>The processors: how many, how busy, and what they are.</summary>
    public static class Cpu
    {
        /// <summary>What a load average is measured against.</summary>
        public enum CpuLoadScope
        {
            /// <summary>The machine's whole load.</summary>
            All,

            /// <summary>The load divided by the processors online.</summary>
            PerProcessor,
        }

        /// <summary>Which of the system's load averages to read.</summary>
        public enum CpuLoadWindow
        {
            /// <summary>The average over one minute.</summary>
            OneMinute,

            /// <summary>The average over five minutes.</summary>
            FiveMinutes,

            /// <summary>The average over fifteen minutes.</summary>
            FifteenMinutes,
        }

        /// <summary>How many processors are online: <c>system.cpu.num</c>.</summary>
        public static ZabbixItemKey<long> Count { get; } = Item<long>("system.cpu.num");

        /// <summary>How many processors the machine has configured, online or not: <c>system.cpu.num[max]</c>.</summary>
        /// <remarks>Linux and macOS only.</remarks>
        public static ZabbixItemKey<long> ConfiguredCount { get; } = Item<long>("system.cpu.num", "max");

        /// <summary>The processors and whether each is online, as low-level discovery JSON: <c>system.cpu.discovery</c>.</summary>
        public static ZabbixItemKey<string> Discovery { get; } = Item<string>("system.cpu.discovery");

        /// <summary>The system's load average: <c>system.cpu.load[cpu,mode]</c>.</summary>
        /// <param name="scope">Whether the load is divided among the processors.</param>
        /// <param name="window">Which of the load averages to read.</param>
        /// <returns>The item for that load average.</returns>
        /// <remarks>Linux and macOS only.</remarks>
        public static ZabbixItemKey<double> Load(CpuLoadScope scope = CpuLoadScope.All, CpuLoadWindow window = CpuLoadWindow.OneMinute)
            => Item<double>("system.cpu.load", Text(scope), Text(window));

        /// <summary>The percentage of time spent running ordinary user code: <c>system.cpu.util[cpu,user,mode]</c>.</summary>
        /// <param name="processor">The processor's number, from zero; all of them when left out.</param>
        /// <param name="window">How far back the average reaches.</param>
        /// <returns>The item for that processor and window.</returns>
        /// <remarks>Linux and macOS only.</remarks>
        public static ZabbixItemKey<double> UserPercent(int? processor = null, SampleWindow window = SampleWindow.OneMinute)
            => Utilization("user", processor, window);

        /// <summary>The percentage of time spent running user code at low priority: <c>system.cpu.util[cpu,nice,mode]</c>.</summary>
        /// <param name="processor">The processor's number, from zero; all of them when left out.</param>
        /// <param name="window">How far back the average reaches.</param>
        /// <returns>The item for that processor and window.</returns>
        /// <remarks>Linux and macOS only.</remarks>
        public static ZabbixItemKey<double> NicePercent(int? processor = null, SampleWindow window = SampleWindow.OneMinute)
            => Utilization("nice", processor, window);

        /// <summary>The percentage of time spent running kernel code: <c>system.cpu.util[cpu,system,mode]</c>.</summary>
        /// <param name="processor">The processor's number, from zero; all of them when left out.</param>
        /// <param name="window">How far back the average reaches.</param>
        /// <returns>The item for that processor and window.</returns>
        /// <remarks>On Windows, for all the processors together only, where it is their whole busy time.</remarks>
        public static ZabbixItemKey<double> SystemPercent(int? processor = null, SampleWindow window = SampleWindow.OneMinute)
            => Utilization("system", processor, window);

        /// <summary>The percentage of time spent with nothing to run: <c>system.cpu.util[cpu,idle,mode]</c>.</summary>
        /// <param name="processor">The processor's number, from zero; all of them when left out.</param>
        /// <param name="window">How far back the average reaches.</param>
        /// <returns>The item for that processor and window.</returns>
        /// <remarks>Linux and macOS only.</remarks>
        public static ZabbixItemKey<double> IdlePercent(int? processor = null, SampleWindow window = SampleWindow.OneMinute)
            => Utilization("idle", processor, window);

        /// <summary>The percentage of time spent idle with disk or network input and output pending: <c>system.cpu.util[cpu,iowait,mode]</c>.</summary>
        /// <param name="processor">The processor's number, from zero; all of them when left out.</param>
        /// <param name="window">How far back the average reaches.</param>
        /// <returns>The item for that processor and window.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<double> IoWaitPercent(int? processor = null, SampleWindow window = SampleWindow.OneMinute)
            => Utilization("iowait", processor, window);

        /// <summary>The percentage of time spent serving hardware interrupts: <c>system.cpu.util[cpu,interrupt,mode]</c>.</summary>
        /// <param name="processor">The processor's number, from zero; all of them when left out.</param>
        /// <param name="window">How far back the average reaches.</param>
        /// <returns>The item for that processor and window.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<double> InterruptPercent(int? processor = null, SampleWindow window = SampleWindow.OneMinute)
            => Utilization("interrupt", processor, window);

        /// <summary>The percentage of time spent serving software interrupts: <c>system.cpu.util[cpu,softirq,mode]</c>.</summary>
        /// <param name="processor">The processor's number, from zero; all of them when left out.</param>
        /// <param name="window">How far back the average reaches.</param>
        /// <returns>The item for that processor and window.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<double> SoftIrqPercent(int? processor = null, SampleWindow window = SampleWindow.OneMinute)
            => Utilization("softirq", processor, window);

        /// <summary>The percentage of time a hypervisor gave this machine's processors to another: <c>system.cpu.util[cpu,steal,mode]</c>.</summary>
        /// <param name="processor">The processor's number, from zero; all of them when left out.</param>
        /// <param name="window">How far back the average reaches.</param>
        /// <returns>The item for that processor and window.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<double> StealPercent(int? processor = null, SampleWindow window = SampleWindow.OneMinute)
            => Utilization("steal", processor, window);

        /// <summary>The percentage of time spent running virtual machines: <c>system.cpu.util[cpu,guest,mode]</c>.</summary>
        /// <param name="processor">The processor's number, from zero; all of them when left out.</param>
        /// <param name="window">How far back the average reaches.</param>
        /// <returns>The item for that processor and window.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<double> GuestPercent(int? processor = null, SampleWindow window = SampleWindow.OneMinute)
            => Utilization("guest", processor, window);

        /// <summary>The percentage of time spent running virtual machines at low priority: <c>system.cpu.util[cpu,guest_nice,mode]</c>.</summary>
        /// <param name="processor">The processor's number, from zero; all of them when left out.</param>
        /// <param name="window">How far back the average reaches.</param>
        /// <returns>The item for that processor and window.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<double> GuestNicePercent(int? processor = null, SampleWindow window = SampleWindow.OneMinute)
            => Utilization("guest_nice", processor, window);

        /// <summary>How many context switches there have been since the machine started: <c>system.cpu.switches</c>.</summary>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> ContextSwitches { get; } = Item<ulong>("system.cpu.switches");

        /// <summary>How many device interrupts there have been since the machine started: <c>system.cpu.intr</c>.</summary>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> Interrupts { get; } = Item<ulong>("system.cpu.intr");

        /// <summary>A processor's vendor, model and frequencies in a line of text, or a line for each processor: <c>system.hw.cpu[cpu]</c>.</summary>
        /// <param name="processor">The processor's number, from zero; all of them when left out.</param>
        /// <returns>The item for that processor.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<string> Description(int? processor = null)
            => Item<string>("system.hw.cpu", Whole(processor, 0));

        /// <summary>A processor's vendor, or a line naming each processor's: <c>system.hw.cpu[cpu,vendor]</c>.</summary>
        /// <param name="processor">The processor's number, from zero; all of them when left out.</param>
        /// <returns>The item for that processor.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<string> Vendor(int? processor = null)
            => Item<string>("system.hw.cpu", Whole(processor, 0), "vendor");

        /// <summary>A processor's model name, or a line naming each processor's: <c>system.hw.cpu[cpu,model]</c>.</summary>
        /// <param name="processor">The processor's number, from zero; all of them when left out.</param>
        /// <returns>The item for that processor.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<string> Model(int? processor = null)
            => Item<string>("system.hw.cpu", Whole(processor, 0), "model");

        /// <summary>Every processor's highest frequency, as a line of text in megahertz for each: <c>system.hw.cpu[,maxfreq]</c>.</summary>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<string> MaxFrequencies { get; } = Item<string>("system.hw.cpu", string.Empty, "maxfreq");

        /// <summary>Every processor's present frequency, as a line of text in megahertz for each: <c>system.hw.cpu[,curfreq]</c>.</summary>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<string> CurrentFrequencies { get; } = Item<string>("system.hw.cpu", string.Empty, "curfreq");

        /// <summary>One processor's highest frequency, in hertz: <c>system.hw.cpu[cpu,maxfreq]</c>.</summary>
        /// <param name="processor">The processor's number, from zero.</param>
        /// <returns>The item for that processor.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<long> MaxFrequencyHertz(int processor)
            => Item<long>("system.hw.cpu", Whole(processor, 0), "maxfreq");

        /// <summary>One processor's present frequency, in hertz: <c>system.hw.cpu[cpu,curfreq]</c>.</summary>
        /// <param name="processor">The processor's number, from zero.</param>
        /// <returns>The item for that processor.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<long> CurrentFrequencyHertz(int processor)
            => Item<long>("system.hw.cpu", Whole(processor, 0), "curfreq");

        private static ZabbixItemKey<double> Utilization(string state, int? processor, SampleWindow window)
            => Item<double>("system.cpu.util", Whole(processor, 0), state, Window(window));

        private static KeyPart Text(CpuLoadScope scope) => scope switch
        {
            CpuLoadScope.All => "all",
            CpuLoadScope.PerProcessor => "percpu",
            _ => throw Undefined(scope),
        };

        private static KeyPart Text(CpuLoadWindow window) => window switch
        {
            CpuLoadWindow.OneMinute => "avg1",
            CpuLoadWindow.FiveMinutes => "avg5",
            CpuLoadWindow.FifteenMinutes => "avg15",
            _ => throw Undefined(window),
        };
    }

    /// <summary>The machine's memory: the physical memory, and on Windows the commit limit.</summary>
    public static class Memory
    {
        /// <summary>The physical memory the machine has, in bytes: <c>vm.memory.size[total]</c>.</summary>
        public static ZabbixItemKey<long> TotalBytes { get; } = Item<long>("vm.memory.size", "total");

        /// <summary>The memory nothing is using, in bytes: <c>vm.memory.size[free]</c>.</summary>
        public static ZabbixItemKey<long> FreeBytes { get; } = Item<long>("vm.memory.size", "free");

        /// <summary>The memory in use, in bytes: <c>vm.memory.size[used]</c>.</summary>
        public static ZabbixItemKey<long> UsedBytes { get; } = Item<long>("vm.memory.size", "used");

        /// <summary>The memory in use, as a percentage of the total: <c>vm.memory.size[pused]</c>.</summary>
        public static ZabbixItemKey<double> UsedPercent { get; } = Item<double>("vm.memory.size", "pused");

        /// <summary>The memory a new process could be given, in bytes: <c>vm.memory.size[available]</c>.</summary>
        public static ZabbixItemKey<long> AvailableBytes { get; } = Item<long>("vm.memory.size", "available");

        /// <summary>The memory a new process could be given, as a percentage of the total: <c>vm.memory.size[pavailable]</c>.</summary>
        public static ZabbixItemKey<double> AvailablePercent { get; } = Item<double>("vm.memory.size", "pavailable");

        /// <summary>The memory holding cached file data, in bytes: <c>vm.memory.size[cached]</c>.</summary>
        /// <remarks>Linux and Windows only.</remarks>
        public static ZabbixItemKey<long> CachedBytes { get; } = Item<long>("vm.memory.size", "cached");

        /// <summary>The memory the kernel holds as block device buffers, in bytes: <c>vm.memory.size[buffers]</c>.</summary>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<long> BuffersBytes { get; } = Item<long>("vm.memory.size", "buffers");

        /// <summary>The memory used recently, in bytes: <c>vm.memory.size[active]</c>.</summary>
        /// <remarks>Linux and macOS only.</remarks>
        public static ZabbixItemKey<long> ActiveBytes { get; } = Item<long>("vm.memory.size", "active");

        /// <summary>The memory not used recently, in bytes: <c>vm.memory.size[inactive]</c>.</summary>
        /// <remarks>Linux and macOS only.</remarks>
        public static ZabbixItemKey<long> InactiveBytes { get; } = Item<long>("vm.memory.size", "inactive");

        /// <summary>The memory no file backs, mapped into processes, in bytes: <c>vm.memory.size[anon]</c>.</summary>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<long> AnonymousBytes { get; } = Item<long>("vm.memory.size", "anon");

        /// <summary>The memory the kernel's own data structures hold, in bytes: <c>vm.memory.size[slab]</c>.</summary>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<long> SlabBytes { get; } = Item<long>("vm.memory.size", "slab");

        /// <summary>The memory shared between processes, in bytes: <c>vm.memory.size[shared]</c>.</summary>
        /// <remarks>Only Linux 2.4 reports this.</remarks>
        public static ZabbixItemKey<long> SharedBytes { get; } = Item<long>("vm.memory.size", "shared");

        /// <summary>The memory that can't be paged out, in bytes: <c>vm.memory.size[wired]</c>.</summary>
        /// <remarks>macOS only.</remarks>
        public static ZabbixItemKey<long> WiredBytes { get; } = Item<long>("vm.memory.size", "wired");

        /// <summary>The commit limit, in bytes: <c>vm.vmemory.size[total]</c>.</summary>
        /// <remarks>Windows only.</remarks>
        public static ZabbixItemKey<long> VirtualTotalBytes { get; } = Item<long>("vm.vmemory.size", "total");

        /// <summary>The memory committed so far, in bytes: <c>vm.vmemory.size[used]</c>.</summary>
        /// <remarks>Windows only.</remarks>
        public static ZabbixItemKey<long> VirtualUsedBytes { get; } = Item<long>("vm.vmemory.size", "used");

        /// <summary>What is left of the commit limit, in bytes: <c>vm.vmemory.size[available]</c>.</summary>
        /// <remarks>Windows only.</remarks>
        public static ZabbixItemKey<long> VirtualAvailableBytes { get; } = Item<long>("vm.vmemory.size", "available");

        /// <summary>What is left of the commit limit, as a percentage of it: <c>vm.vmemory.size[pavailable]</c>.</summary>
        /// <remarks>Windows only.</remarks>
        public static ZabbixItemKey<double> VirtualAvailablePercent { get; } = Item<double>("vm.vmemory.size", "pavailable");

        /// <summary>The memory committed so far, as a percentage of the limit: <c>vm.vmemory.size[pused]</c>.</summary>
        /// <remarks>Windows only.</remarks>
        public static ZabbixItemKey<double> VirtualUsedPercent { get; } = Item<double>("vm.vmemory.size", "pused");
    }

    /// <summary>Swap space: how much there is, and how much moves in and out of it.</summary>
    public static class Swap
    {
        /// <summary>The swap space not in use, in bytes: <c>system.swap.size[,free]</c>.</summary>
        public static ZabbixItemKey<long> FreeBytes { get; } = Item<long>("system.swap.size", string.Empty, "free");

        /// <summary>The swap space the machine has, in bytes: <c>system.swap.size[,total]</c>.</summary>
        public static ZabbixItemKey<long> TotalBytes { get; } = Item<long>("system.swap.size", string.Empty, "total");

        /// <summary>The swap space in use, in bytes: <c>system.swap.size[,used]</c>.</summary>
        public static ZabbixItemKey<long> UsedBytes { get; } = Item<long>("system.swap.size", string.Empty, "used");

        /// <summary>The swap space not in use, as a percentage of the total: <c>system.swap.size[,pfree]</c>.</summary>
        public static ZabbixItemKey<double> FreePercent { get; } = Item<double>("system.swap.size", string.Empty, "pfree");

        /// <summary>The swap space in use, as a percentage of the total: <c>system.swap.size[,pused]</c>.</summary>
        /// <remarks>Linux and macOS only.</remarks>
        public static ZabbixItemKey<double> UsedPercent { get; } = Item<double>("system.swap.size", string.Empty, "pused");

        /// <summary>How many pages have been swapped in since the machine started: <c>system.swap.in[,pages]</c>.</summary>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> PagesIn { get; } = Item<ulong>("system.swap.in", string.Empty, "pages");

        /// <summary>How many pages have been swapped out since the machine started: <c>system.swap.out[,pages]</c>.</summary>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> PagesOut { get; } = Item<ulong>("system.swap.out", string.Empty, "pages");

        /// <summary>How many read operations swap devices have done: <c>system.swap.in[device,count]</c>.</summary>
        /// <param name="device">The swap device to read; all of them when left out.</param>
        /// <returns>The item for that device.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> InOperations(string? device = null) => Item<ulong>("system.swap.in", device, "count");

        /// <summary>How many sectors swap devices have read: <c>system.swap.in[device,sectors]</c>.</summary>
        /// <param name="device">The swap device to read; all of them when left out.</param>
        /// <returns>The item for that device.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> InSectors(string? device = null) => Item<ulong>("system.swap.in", device, "sectors");

        /// <summary>How many write operations swap devices have done: <c>system.swap.out[device,count]</c>.</summary>
        /// <param name="device">The swap device to read; all of them when left out.</param>
        /// <returns>The item for that device.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> OutOperations(string? device = null) => Item<ulong>("system.swap.out", device, "count");

        /// <summary>How many sectors swap devices have written: <c>system.swap.out[device,sectors]</c>.</summary>
        /// <param name="device">The swap device to read; all of them when left out.</param>
        /// <returns>The item for that device.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> OutSectors(string? device = null) => Item<ulong>("system.swap.out", device, "sectors");
    }

    /// <summary>The mounted file systems, their space and inodes, and the block devices under them.</summary>
    public static class Disk
    {
        /// <summary>The mounted file systems, as low-level discovery JSON: <c>vfs.fs.discovery</c>.</summary>
        public static ZabbixItemKey<string> Discovery { get; } = Item<string>("vfs.fs.discovery");

        /// <summary>The mounted file systems with their space and inodes, as JSON: <c>vfs.fs.get</c>.</summary>
        public static ZabbixItemKey<string> FileSystems { get; } = Item<string>("vfs.fs.get");

        /// <summary>The size of a file system, in bytes: <c>vfs.fs.size[path,total]</c>.</summary>
        /// <param name="path">The file system's mount point or drive.</param>
        /// <returns>The item for that file system.</returns>
        public static ZabbixItemKey<long> TotalBytes(string path) => Item<long>("vfs.fs.size", Required(path), "total");

        /// <summary>The space a user may still write on a file system, in bytes, without its reserved blocks: <c>vfs.fs.size[path,free]</c>.</summary>
        /// <param name="path">The file system's mount point or drive.</param>
        /// <returns>The item for that file system.</returns>
        public static ZabbixItemKey<long> FreeBytes(string path) => Item<long>("vfs.fs.size", Required(path), "free");

        /// <summary>The space written on a file system, in bytes: <c>vfs.fs.size[path,used]</c>.</summary>
        /// <param name="path">The file system's mount point or drive.</param>
        /// <returns>The item for that file system.</returns>
        public static ZabbixItemKey<long> UsedBytes(string path) => Item<long>("vfs.fs.size", Required(path), "used");

        /// <summary>The space a user may still write, as a percentage of used and free space together: <c>vfs.fs.size[path,pfree]</c>.</summary>
        /// <param name="path">The file system's mount point or drive.</param>
        /// <returns>The item for that file system.</returns>
        public static ZabbixItemKey<double> FreePercent(string path) => Item<double>("vfs.fs.size", Required(path), "pfree");

        /// <summary>The space written, as a percentage of used and free space together: <c>vfs.fs.size[path,pused]</c>.</summary>
        /// <param name="path">The file system's mount point or drive.</param>
        /// <returns>The item for that file system.</returns>
        public static ZabbixItemKey<double> UsedPercent(string path) => Item<double>("vfs.fs.size", Required(path), "pused");

        /// <summary>How many inodes a file system has: <c>vfs.fs.inode[path,total]</c>.</summary>
        /// <param name="path">The file system's mount point.</param>
        /// <returns>The item for that file system.</returns>
        /// <remarks>Linux and macOS only, and not on 32-bit Linux.</remarks>
        public static ZabbixItemKey<ulong> InodesTotal(string path) => Item<ulong>("vfs.fs.inode", Required(path), "total");

        /// <summary>How many of a file system's inodes are free: <c>vfs.fs.inode[path,free]</c>.</summary>
        /// <param name="path">The file system's mount point.</param>
        /// <returns>The item for that file system.</returns>
        /// <remarks>Linux and macOS only, and not on 32-bit Linux.</remarks>
        public static ZabbixItemKey<ulong> InodesFree(string path) => Item<ulong>("vfs.fs.inode", Required(path), "free");

        /// <summary>How many of a file system's inodes are in use: <c>vfs.fs.inode[path,used]</c>.</summary>
        /// <param name="path">The file system's mount point.</param>
        /// <returns>The item for that file system.</returns>
        /// <remarks>Linux and macOS only, and not on 32-bit Linux.</remarks>
        public static ZabbixItemKey<ulong> InodesUsed(string path) => Item<ulong>("vfs.fs.inode", Required(path), "used");

        /// <summary>The free share of a file system's inodes, as a percentage: <c>vfs.fs.inode[path,pfree]</c>.</summary>
        /// <param name="path">The file system's mount point.</param>
        /// <returns>The item for that file system.</returns>
        /// <remarks>Linux and macOS only, and not on 32-bit Linux.</remarks>
        public static ZabbixItemKey<double> InodesFreePercent(string path) => Item<double>("vfs.fs.inode", Required(path), "pfree");

        /// <summary>The used share of a file system's inodes, as a percentage: <c>vfs.fs.inode[path,pused]</c>.</summary>
        /// <param name="path">The file system's mount point.</param>
        /// <returns>The item for that file system.</returns>
        /// <remarks>Linux and macOS only, and not on 32-bit Linux.</remarks>
        public static ZabbixItemKey<double> InodesUsedPercent(string path) => Item<double>("vfs.fs.inode", Required(path), "pused");

        /// <summary>The block devices, as low-level discovery JSON: <c>vfs.dev.discovery</c>.</summary>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<string> DeviceDiscovery { get; } = Item<string>("vfs.dev.discovery");

        /// <summary>The average sectors read per second: <c>vfs.dev.read[device,sps,mode]</c>.</summary>
        /// <param name="device">The block device to read; all of them when left out.</param>
        /// <param name="window">How far back the average reaches.</param>
        /// <returns>The item for that device and window.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<double> ReadSectorsPerSecond(string? device = null, SampleWindow window = SampleWindow.OneMinute)
            => Item<double>("vfs.dev.read", device, "sps", Window(window));

        /// <summary>The average read operations per second: <c>vfs.dev.read[device,ops,mode]</c>.</summary>
        /// <param name="device">The block device to read; all of them when left out.</param>
        /// <param name="window">How far back the average reaches.</param>
        /// <returns>The item for that device and window.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<double> ReadOperationsPerSecond(string? device = null, SampleWindow window = SampleWindow.OneMinute)
            => Item<double>("vfs.dev.read", device, "ops", Window(window));

        /// <summary>How many sectors have been read since the machine started: <c>vfs.dev.read[device,sectors]</c>.</summary>
        /// <param name="device">The block device to read; all of them when left out.</param>
        /// <returns>The item for that device.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> ReadSectors(string? device = null) => Item<ulong>("vfs.dev.read", device, "sectors");

        /// <summary>How many read operations there have been since the machine started: <c>vfs.dev.read[device,operations]</c>.</summary>
        /// <param name="device">The block device to read; all of them when left out.</param>
        /// <returns>The item for that device.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> ReadOperations(string? device = null) => Item<ulong>("vfs.dev.read", device, "operations");

        /// <summary>The average sectors written per second: <c>vfs.dev.write[device,sps,mode]</c>.</summary>
        /// <param name="device">The block device to read; all of them when left out.</param>
        /// <param name="window">How far back the average reaches.</param>
        /// <returns>The item for that device and window.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<double> WriteSectorsPerSecond(string? device = null, SampleWindow window = SampleWindow.OneMinute)
            => Item<double>("vfs.dev.write", device, "sps", Window(window));

        /// <summary>The average write operations per second: <c>vfs.dev.write[device,ops,mode]</c>.</summary>
        /// <param name="device">The block device to read; all of them when left out.</param>
        /// <param name="window">How far back the average reaches.</param>
        /// <returns>The item for that device and window.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<double> WriteOperationsPerSecond(string? device = null, SampleWindow window = SampleWindow.OneMinute)
            => Item<double>("vfs.dev.write", device, "ops", Window(window));

        /// <summary>How many sectors have been written since the machine started: <c>vfs.dev.write[device,sectors]</c>.</summary>
        /// <param name="device">The block device to read; all of them when left out.</param>
        /// <returns>The item for that device.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> WriteSectors(string? device = null) => Item<ulong>("vfs.dev.write", device, "sectors");

        /// <summary>How many write operations there have been since the machine started: <c>vfs.dev.write[device,operations]</c>.</summary>
        /// <param name="device">The block device to read; all of them when left out.</param>
        /// <returns>The item for that device.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> WriteOperations(string? device = null) => Item<ulong>("vfs.dev.write", device, "operations");
    }

    /// <summary>Single files and what a directory holds.</summary>
    public static class File
    {
        private static readonly ImmutableArray<(FileTypes Type, string Text)> s_typeTexts =
        [
            (FileTypes.File, "file"),
            (FileTypes.Directory, "dir"),
            (FileTypes.SymbolicLink, "sym"),
            (FileTypes.Socket, "sock"),
            (FileTypes.BlockDevice, "bdev"),
            (FileTypes.CharacterDevice, "cdev"),
            (FileTypes.Fifo, "fifo"),
        ];

        /// <summary>The kinds of file a key can filter on. Windows has only files, directories and symbolic links.</summary>
        [Flags]
        public enum FileTypes
        {
            /// <summary>No filter: leave the choice to the key.</summary>
            None = 0,

            /// <summary>A regular file.</summary>
            File = 1 << 0,

            /// <summary>A directory.</summary>
            Directory = 1 << 1,

            /// <summary>A symbolic link.</summary>
            SymbolicLink = 1 << 2,

            /// <summary>A socket.</summary>
            Socket = 1 << 3,

            /// <summary>A block device.</summary>
            BlockDevice = 1 << 4,

            /// <summary>A character device.</summary>
            CharacterDevice = 1 << 5,

            /// <summary>A named pipe.</summary>
            Fifo = 1 << 6,

            /// <summary>A block or character device.</summary>
            Device = BlockDevice | CharacterDevice,

            /// <summary>Every kind of file.</summary>
            All = File | Directory | SymbolicLink | Socket | BlockDevice | CharacterDevice | Fifo,
        }

        /// <summary>Whether a path exists and is of a kind asked for: <c>vfs.file.exists[path,types,excluded types]</c>.</summary>
        /// <param name="path">The path to look at.</param>
        /// <param name="include">The kinds that count; only regular files when this and the exclusion are both left out, otherwise every kind.</param>
        /// <param name="exclude">The kinds that make the answer <see langword="false"/>, whatever else the path is.</param>
        /// <returns>The item for that path.</returns>
        public static ZabbixItemKey<bool> Exists(string path, FileTypes include = FileTypes.None, FileTypes exclude = FileTypes.None)
            => Item<bool>("vfs.file.exists", Required(path), Text(include), Text(exclude));

        /// <summary>A file's size, in bytes: <c>vfs.file.size[path,bytes]</c>.</summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The item for that file.</returns>
        public static ZabbixItemKey<long> SizeBytes(string path) => Item<long>("vfs.file.size", Required(path), "bytes");

        /// <summary>How many newlines a file holds: <c>vfs.file.size[path,lines]</c>.</summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The item for that file.</returns>
        public static ZabbixItemKey<long> LineCount(string path) => Item<long>("vfs.file.size", Required(path), "lines");

        /// <summary>When a file's contents last changed: <c>vfs.file.time[path,modify]</c>.</summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The item for that file.</returns>
        public static ZabbixItemKey<DateTimeOffset> ModifiedTime(string path) => Item<DateTimeOffset>("vfs.file.time", Required(path), "modify");

        /// <summary>When a file was last read: <c>vfs.file.time[path,access]</c>.</summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The item for that file.</returns>
        public static ZabbixItemKey<DateTimeOffset> AccessedTime(string path) => Item<DateTimeOffset>("vfs.file.time", Required(path), "access");

        /// <summary>When a file's contents or attributes last changed: <c>vfs.file.time[path,change]</c>.</summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The item for that file.</returns>
        public static ZabbixItemKey<DateTimeOffset> ChangedTime(string path) => Item<DateTimeOffset>("vfs.file.time", Required(path), "change");

        /// <summary>The name of the account that owns a file, or its id when the account has no name: <c>vfs.file.owner[path,user,name]</c>.</summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The item for that file.</returns>
        public static ZabbixItemKey<string> OwnerName(string path) => Item<string>("vfs.file.owner", Required(path), "user", "name");

        /// <summary>
        /// The id of the account that owns a file: <c>vfs.file.owner[path,user,id]</c>. It is text because a Windows id is a SID, not
        /// a number.
        /// </summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The item for that file.</returns>
        public static ZabbixItemKey<string> OwnerId(string path) => Item<string>("vfs.file.owner", Required(path), "user", "id");

        /// <summary>The name of the group that owns a file, or its id when the group has no name: <c>vfs.file.owner[path,group,name]</c>.</summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The item for that file.</returns>
        /// <remarks>Linux and macOS only.</remarks>
        public static ZabbixItemKey<string> GroupName(string path) => Item<string>("vfs.file.owner", Required(path), "group", "name");

        /// <summary>The id of the group that owns a file, as text like <see cref="OwnerId"/>: <c>vfs.file.owner[path,group,id]</c>.</summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The item for that file.</returns>
        /// <remarks>Linux and macOS only.</remarks>
        public static ZabbixItemKey<string> GroupId(string path) => Item<string>("vfs.file.owner", Required(path), "group", "id");

        /// <summary>A file's type, owner, permissions, size and times, as JSON: <c>vfs.file.get[path]</c>.</summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The item for that file.</returns>
        public static ZabbixItemKey<string> Details(string path) => Item<string>("vfs.file.get", Required(path));

        /// <summary>The text of a file of up to 16 MB, without a byte order mark or trailing newlines: <c>vfs.file.contents[path,encoding]</c>.</summary>
        /// <param name="path">The path of the file.</param>
        /// <param name="encoding">The name of the file's encoding; when left out, its byte order mark or else UTF-8.</param>
        /// <returns>The item for that file.</returns>
        public static ZabbixItemKey<string> Contents(string path, string? encoding = null) => Item<string>("vfs.file.contents", Required(path), encoding);

        /// <summary>
        /// The first line of a file that matches a regular expression, or empty text when none does:
        /// <c>vfs.file.regexp[path,pattern,encoding,start line,end line,output]</c>.
        /// </summary>
        /// <param name="path">The path of the file.</param>
        /// <param name="pattern">The regular expression a line must match.</param>
        /// <param name="encoding">The name of the file's encoding; when left out, its byte order mark or else UTF-8.</param>
        /// <param name="startLine">The first line to search, from one; the file's first when left out.</param>
        /// <param name="endLine">The last line to search, from one; the file's last when left out.</param>
        /// <param name="output">What to answer instead of the line, where <c>\0</c> is the match and <c>\1</c> to <c>\9</c> its groups.</param>
        /// <returns>The item for that search.</returns>
        public static ZabbixItemKey<string> FirstMatchingLine(string path, string pattern, string? encoding = null, int? startLine = null, int? endLine = null, string? output = null)
            => Item<string>("vfs.file.regexp", Required(path), Required(pattern), encoding, Whole(startLine, 1), Whole(endLine, 1), output);

        /// <summary>Whether any line of a file matches a regular expression: <c>vfs.file.regmatch[path,pattern,encoding,start line,end line]</c>.</summary>
        /// <param name="path">The path of the file.</param>
        /// <param name="pattern">The regular expression a line must match.</param>
        /// <param name="encoding">The name of the file's encoding; when left out, its byte order mark or else UTF-8.</param>
        /// <param name="startLine">The first line to search, from one; the file's first when left out.</param>
        /// <param name="endLine">The last line to search, from one; the file's last when left out.</param>
        /// <returns>The item for that search.</returns>
        public static ZabbixItemKey<bool> HasMatchingLine(string path, string pattern, string? encoding = null, int? startLine = null, int? endLine = null)
            => Item<bool>("vfs.file.regmatch", Required(path), Required(pattern), encoding, Whole(startLine, 1), Whole(endLine, 1));

        /// <summary>A file's MD5 sum, in hexadecimal: <c>vfs.file.md5sum[path]</c>.</summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The item for that file.</returns>
        public static ZabbixItemKey<string> Md5Sum(string path) => Item<string>("vfs.file.md5sum", Required(path));

        /// <summary>A file's POSIX <c>cksum</c> CRC, as a number: <c>vfs.file.cksum[path,crc32]</c>.</summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The item for that file.</returns>
        public static ZabbixItemKey<long> Crc32Checksum(string path) => Item<long>("vfs.file.cksum", Required(path), "crc32");

        /// <summary>A file's MD5 sum, in hexadecimal: <c>vfs.file.cksum[path,md5]</c>.</summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The item for that file.</returns>
        public static ZabbixItemKey<string> Md5Checksum(string path) => Item<string>("vfs.file.cksum", Required(path), "md5");

        /// <summary>A file's SHA-256 sum, in hexadecimal: <c>vfs.file.cksum[path,sha256]</c>.</summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The item for that file.</returns>
        public static ZabbixItemKey<string> Sha256Checksum(string path) => Item<string>("vfs.file.cksum", Required(path), "sha256");

        /// <summary>A file's UNIX permissions, as text of four octal digits such as <c>0644</c>: <c>vfs.file.permissions[path]</c>.</summary>
        /// <param name="path">The path of the file.</param>
        /// <returns>The item for that file.</returns>
        /// <remarks>Linux and macOS only.</remarks>
        public static ZabbixItemKey<string> Permissions(string path) => Item<string>("vfs.file.permissions", Required(path));

        /// <summary>
        /// How many entries under a directory match:
        /// <c>vfs.dir.count[path,pattern,excluded pattern,types,excluded types,depth,least size,most size,least age,most age,excluded directories]</c>.
        /// </summary>
        /// <param name="path">The directory to walk.</param>
        /// <param name="includePattern">A regular expression an entry's name must match.</param>
        /// <param name="excludePattern">A regular expression an entry's name must not match.</param>
        /// <param name="includeTypes">The kinds of entry that count; every kind when left out.</param>
        /// <param name="excludeTypes">The kinds of entry that don't count.</param>
        /// <param name="maxDepth">How many levels below the directory to walk; all of them when left out.</param>
        /// <param name="minSize">The smallest size that counts, in bytes.</param>
        /// <param name="maxSize">The largest size that counts, in bytes.</param>
        /// <param name="minAge">The shortest time since a last modification that counts, in whole seconds.</param>
        /// <param name="maxAge">The longest time since a last modification that counts, in whole seconds.</param>
        /// <param name="excludeDirectoryPattern">A regular expression that leaves out a directory, matched against its path below <paramref name="path"/>.</param>
        /// <returns>The item for that walk.</returns>
        public static ZabbixItemKey<long> DirectoryEntryCount(
            string path,
            string? includePattern = null,
            string? excludePattern = null,
            FileTypes includeTypes = FileTypes.None,
            FileTypes excludeTypes = FileTypes.None,
            int? maxDepth = null,
            long? minSize = null,
            long? maxSize = null,
            TimeSpan? minAge = null,
            TimeSpan? maxAge = null,
            string? excludeDirectoryPattern = null)
            => Item<long>(
                "vfs.dir.count", Required(path), includePattern, excludePattern, Text(includeTypes), Text(excludeTypes), Whole(maxDepth, 0),
                Whole(minSize, 0), Whole(maxSize, 0), Seconds(minAge, 0), Seconds(maxAge, 0), excludeDirectoryPattern);

        /// <summary>
        /// The entries under a directory that match, each described as <see cref="Details"/> describes a file, as JSON:
        /// <c>vfs.dir.get[path,pattern,excluded pattern,types,excluded types,depth,least size,most size,least age,most age,excluded directories]</c>.
        /// </summary>
        /// <param name="path">The directory to walk.</param>
        /// <param name="includePattern">A regular expression an entry's name must match.</param>
        /// <param name="excludePattern">A regular expression an entry's name must not match.</param>
        /// <param name="includeTypes">The kinds of entry that count; every kind when left out.</param>
        /// <param name="excludeTypes">The kinds of entry that don't count.</param>
        /// <param name="maxDepth">How many levels below the directory to walk; all of them when left out.</param>
        /// <param name="minSize">The smallest size that counts, in bytes.</param>
        /// <param name="maxSize">The largest size that counts, in bytes.</param>
        /// <param name="minAge">The shortest time since a last modification that counts, in whole seconds.</param>
        /// <param name="maxAge">The longest time since a last modification that counts, in whole seconds.</param>
        /// <param name="excludeDirectoryPattern">A regular expression that leaves out a directory, matched against its path below <paramref name="path"/>.</param>
        /// <returns>The item for that walk.</returns>
        public static ZabbixItemKey<string> DirectoryEntries(
            string path,
            string? includePattern = null,
            string? excludePattern = null,
            FileTypes includeTypes = FileTypes.None,
            FileTypes excludeTypes = FileTypes.None,
            int? maxDepth = null,
            long? minSize = null,
            long? maxSize = null,
            TimeSpan? minAge = null,
            TimeSpan? maxAge = null,
            string? excludeDirectoryPattern = null)
            => Item<string>(
                "vfs.dir.get", Required(path), includePattern, excludePattern, Text(includeTypes), Text(excludeTypes), Whole(maxDepth, 0),
                Whole(minSize, 0), Whole(maxSize, 0), Seconds(minAge, 0), Seconds(maxAge, 0), excludeDirectoryPattern);

        /// <summary>
        /// The total size of what a directory holds, in bytes, counting each file's length:
        /// <c>vfs.dir.size[path,pattern,excluded pattern,,depth,excluded directories]</c>.
        /// </summary>
        /// <param name="path">The directory to walk.</param>
        /// <param name="includePattern">A regular expression an entry's name must match.</param>
        /// <param name="excludePattern">A regular expression an entry's name must not match.</param>
        /// <param name="maxDepth">How many levels below the directory to walk; all of them when left out.</param>
        /// <param name="excludeDirectoryPattern">A regular expression that leaves out a directory, matched against its path below <paramref name="path"/>.</param>
        /// <returns>The item for that walk.</returns>
        public static ZabbixItemKey<long> DirectorySizeBytes(string path, string? includePattern = null, string? excludePattern = null, int? maxDepth = null, string? excludeDirectoryPattern = null)
            => Item<long>("vfs.dir.size", Required(path), includePattern, excludePattern, string.Empty, Whole(maxDepth, 0), excludeDirectoryPattern);

        /// <summary>
        /// The disk space what a directory holds takes up, in bytes:
        /// <c>vfs.dir.size[path,pattern,excluded pattern,disk,depth,excluded directories]</c>.
        /// </summary>
        /// <param name="path">The directory to walk.</param>
        /// <param name="includePattern">A regular expression an entry's name must match.</param>
        /// <param name="excludePattern">A regular expression an entry's name must not match.</param>
        /// <param name="maxDepth">How many levels below the directory to walk; all of them when left out.</param>
        /// <param name="excludeDirectoryPattern">A regular expression that leaves out a directory, matched against its path below <paramref name="path"/>.</param>
        /// <returns>The item for that walk.</returns>
        public static ZabbixItemKey<long> DirectoryDiskBytes(string path, string? includePattern = null, string? excludePattern = null, int? maxDepth = null, string? excludeDirectoryPattern = null)
            => Item<long>("vfs.dir.size", Required(path), includePattern, excludePattern, "disk", Whole(maxDepth, 0), excludeDirectoryPattern);

        private static string Text(FileTypes types, [CallerArgumentExpression(nameof(types))] string? parameterName = null)
        {
            if ((types & ~FileTypes.All) != 0) throw Undefined(types, parameterName);
            return types switch
            {
                FileTypes.All => "all",
                FileTypes.Device => "dev",
                _ => string.Join(',', s_typeTexts.Where(entry => (types & entry.Type) != 0).Select(entry => entry.Text)),
            };
        }
    }

    /// <summary>The processes running: how many match, what they use, and their details.</summary>
    public static class Process
    {
        /// <summary>Which state a process must be in to be counted.</summary>
        public enum ProcessState
        {
            /// <summary>Any state.</summary>
            All,

            /// <summary>Running or ready to run.</summary>
            Running,

            /// <summary>Asleep, and able to be woken.</summary>
            Sleeping,

            /// <summary>Finished, and waiting for its parent to read its exit status.</summary>
            Zombie,

            /// <summary>Asleep and unable to be woken, usually waiting for a disk.</summary>
            DiskSleep,

            /// <summary>Stopped or being traced.</summary>
            Traced,
        }

        /// <summary>How the matching processes' whole-number figures combine into one.</summary>
        public enum ProcessMemoryStatistic
        {
            /// <summary>Their sum.</summary>
            Sum,

            /// <summary>The largest of them.</summary>
            Maximum,

            /// <summary>The smallest of them.</summary>
            Minimum,
        }

        /// <summary>Which of a process's memory figures to read.</summary>
        public enum ProcessMemoryType
        {
            /// <summary>The virtual memory it has mapped.</summary>
            VirtualSize,

            /// <summary>What it holds in physical memory now.</summary>
            ResidentSet,

            /// <summary>Its data, stack and code segments together.</summary>
            DataStackAndCode,

            /// <summary>The most virtual memory it has had mapped.</summary>
            PeakVirtualSize,

            /// <summary>What it has swapped out.</summary>
            Swapped,

            /// <summary>Its shared libraries' code.</summary>
            SharedLibraries,

            /// <summary>What it has locked into memory.</summary>
            Locked,

            /// <summary>What it has pinned in memory.</summary>
            Pinned,

            /// <summary>The most it has held in physical memory.</summary>
            PeakResidentSet,

            /// <summary>Its data segment.</summary>
            Data,

            /// <summary>Its stack segment.</summary>
            Stack,

            /// <summary>Its code segment.</summary>
            Code,

            /// <summary>Its page tables.</summary>
            PageTables,
        }

        /// <summary>Which processor time of a process to read.</summary>
        public enum ProcessCpuTime
        {
            /// <summary>All its time.</summary>
            Total,

            /// <summary>Its time running its own code.</summary>
            User,

            /// <summary>Its time running kernel code on its behalf.</summary>
            Privileged,
        }

        /// <summary>Which counter of a Windows process to read.</summary>
        public enum ProcessInfoCounter
        {
            /// <summary>The memory committed to it, in kilobytes.</summary>
            VirtualSizeKilobytes,

            /// <summary>Its working set, in kilobytes.</summary>
            WorkingSetKilobytes,

            /// <summary>How many page faults it has had.</summary>
            PageFaults,

            /// <summary>Its time running kernel code, in milliseconds.</summary>
            KernelTimeMilliseconds,

            /// <summary>Its time running its own code, in milliseconds.</summary>
            UserTimeMilliseconds,

            /// <summary>How many GDI objects it holds.</summary>
            GdiObjects,

            /// <summary>How many USER objects it holds.</summary>
            UserObjects,

            /// <summary>The bytes it has read.</summary>
            IoReadBytes,

            /// <summary>The read operations it has done.</summary>
            IoReadOperations,

            /// <summary>The bytes it has written.</summary>
            IoWriteBytes,

            /// <summary>The write operations it has done.</summary>
            IoWriteOperations,

            /// <summary>The bytes it has moved in operations other than reads and writes.</summary>
            IoOtherBytes,

            /// <summary>The operations it has done other than reads and writes.</summary>
            IoOtherOperations,
        }

        /// <summary>How the counters of the Windows processes of one image combine into one figure.</summary>
        public enum ProcessInfoStatistic
        {
            /// <summary>Their average.</summary>
            Average,

            /// <summary>The smallest of them.</summary>
            Minimum,

            /// <summary>The largest of them.</summary>
            Maximum,

            /// <summary>Their sum.</summary>
            Sum,
        }

        /// <summary>How many processes match: <c>proc.num[name,user,state,command line]</c>.</summary>
        /// <param name="name">The process name to match; every name when left out.</param>
        /// <param name="user">The account that must own the process; any account when left out.</param>
        /// <param name="state">The state the process must be in.</param>
        /// <param name="commandLine">A regular expression the command line must match; any command line when left out.</param>
        /// <returns>The item for that match.</returns>
        /// <remarks>Windows and macOS match by name only, and Windows matches the image name, such as <c>explorer.exe</c>.</remarks>
        public static ZabbixItemKey<long> Count(string? name = null, string? user = null, ProcessState state = ProcessState.All, string? commandLine = null)
            => Item<long>("proc.num", name, user, Text(state), commandLine);

        /// <summary>The memory the matching processes use, in bytes: <c>proc.mem[name,user,statistic,command line,type]</c>.</summary>
        /// <param name="name">The process name to match; every name when left out.</param>
        /// <param name="user">The account that must own the process; any account when left out.</param>
        /// <param name="statistic">How the processes' figures combine into one.</param>
        /// <param name="commandLine">A regular expression the command line must match; any command line when left out.</param>
        /// <param name="memoryType">Which memory figure to read.</param>
        /// <returns>The item for that match.</returns>
        /// <remarks>Windows and macOS match by name only, and read only the virtual size and the resident set.</remarks>
        public static ZabbixItemKey<long> MemoryBytes(
            string? name = null,
            string? user = null,
            ProcessMemoryStatistic statistic = ProcessMemoryStatistic.Sum,
            string? commandLine = null,
            ProcessMemoryType memoryType = ProcessMemoryType.VirtualSize)
            => Item<long>("proc.mem", name, user, Text(statistic), commandLine, Text(memoryType));

        /// <summary>
        /// The average memory per matching process, in bytes, which the agent writes with decimals and so is not a whole number:
        /// <c>proc.mem[name,user,avg,command line,type]</c>.
        /// </summary>
        /// <param name="name">The process name to match; every name when left out.</param>
        /// <param name="user">The account that must own the process; any account when left out.</param>
        /// <param name="commandLine">A regular expression the command line must match; any command line when left out.</param>
        /// <param name="memoryType">Which memory figure to read.</param>
        /// <returns>The item for that match.</returns>
        /// <remarks>Windows and macOS match by name only, and read only the virtual size and the resident set.</remarks>
        public static ZabbixItemKey<double> AverageMemoryBytes(string? name = null, string? user = null, string? commandLine = null, ProcessMemoryType memoryType = ProcessMemoryType.VirtualSize)
            => Item<double>("proc.mem", name, user, "avg", commandLine, Text(memoryType));

        /// <summary>
        /// The physical memory the matching processes hold, as a percentage of the machine's:
        /// <c>proc.mem[name,user,statistic,command line,pmem]</c>.
        /// </summary>
        /// <param name="name">The process name to match; every name when left out.</param>
        /// <param name="user">The account that must own the process; any account when left out.</param>
        /// <param name="statistic">How the processes' figures combine into one.</param>
        /// <param name="commandLine">A regular expression the command line must match; any command line when left out.</param>
        /// <returns>The item for that match.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<double> MemoryPercent(string? name = null, string? user = null, ProcessMemoryStatistic statistic = ProcessMemoryStatistic.Sum, string? commandLine = null)
            => Item<double>("proc.mem", name, user, Text(statistic), commandLine, "pmem");

        /// <summary>
        /// The physical memory an average matching process holds, as a percentage of the machine's:
        /// <c>proc.mem[name,user,avg,command line,pmem]</c>.
        /// </summary>
        /// <param name="name">The process name to match; every name when left out.</param>
        /// <param name="user">The account that must own the process; any account when left out.</param>
        /// <param name="commandLine">A regular expression the command line must match; any command line when left out.</param>
        /// <returns>The item for that match.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<double> AverageMemoryPercent(string? name = null, string? user = null, string? commandLine = null)
            => Item<double>("proc.mem", name, user, "avg", commandLine, "pmem");

        /// <summary>
        /// The processor time the matching processes use, as a percentage of one processor:
        /// <c>proc.cpu.util[name,user,type,command line,mode]</c>.
        /// </summary>
        /// <param name="name">The process name to match; every name when left out.</param>
        /// <param name="user">The account that must own the process; any account when left out.</param>
        /// <param name="time">Which of the processes' processor times to read.</param>
        /// <param name="commandLine">A regular expression the command line must match; any command line when left out.</param>
        /// <param name="window">How far back the average reaches.</param>
        /// <returns>The item for that match.</returns>
        /// <remarks>Windows and macOS match by name only.</remarks>
        public static ZabbixItemKey<double> CpuPercent(
            string? name = null,
            string? user = null,
            ProcessCpuTime time = ProcessCpuTime.Total,
            string? commandLine = null,
            SampleWindow window = SampleWindow.OneMinute)
            => Item<double>("proc.cpu.util", name, user, Text(time), commandLine, Window(window));

        /// <summary>Each matching process's details, as JSON: <c>proc.get[name,user,command line]</c>.</summary>
        /// <param name="name">The process name to match; every name when left out.</param>
        /// <param name="user">The account that must own the process; any account when left out.</param>
        /// <param name="commandLine">A regular expression the command line must match; any command line when left out.</param>
        /// <returns>The item for that match.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<string> Details(string? name = null, string? user = null, string? commandLine = null)
            => Item<string>("proc.get", name, user, commandLine);

        /// <summary>Each thread of the matching processes, as JSON: <c>proc.get[name,user,command line,thread]</c>.</summary>
        /// <param name="name">The process name to match; every name when left out.</param>
        /// <param name="user">The account that must own the process; any account when left out.</param>
        /// <param name="commandLine">A regular expression the command line must match; any command line when left out.</param>
        /// <returns>The item for that match.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<string> ThreadDetails(string? name = null, string? user = null, string? commandLine = null)
            => Item<string>("proc.get", name, user, commandLine, "thread");

        /// <summary>The matching processes' figures added up by name, as JSON: <c>proc.get[name,user,,summary]</c>. The key takes no command line in this mode.</summary>
        /// <param name="name">The process name to match; every name when left out.</param>
        /// <param name="user">The account that must own the process; any account when left out.</param>
        /// <returns>The item for that match.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<string> Summary(string? name = null, string? user = null)
            => Item<string>("proc.get", name, user, string.Empty, "summary");

        /// <summary>
        /// A counter of the Windows processes of one image, which the agent writes with decimals whatever the counter:
        /// <c>proc_info[name,attribute,type]</c>.
        /// </summary>
        /// <param name="name">The image name, such as <c>explorer.exe</c>.</param>
        /// <param name="counter">Which of the processes' counters to read.</param>
        /// <param name="statistic">How the processes' counters combine into one.</param>
        /// <returns>The item for that image.</returns>
        /// <remarks>Windows only.</remarks>
        public static ZabbixItemKey<double> Info(string name, ProcessInfoCounter counter = ProcessInfoCounter.VirtualSizeKilobytes, ProcessInfoStatistic statistic = ProcessInfoStatistic.Average)
            => Item<double>("proc_info", Required(name), Text(counter), Text(statistic));

        private static KeyPart Text(ProcessState state) => state switch
        {
            ProcessState.All => Default("all"),
            ProcessState.Running => "run",
            ProcessState.Sleeping => "sleep",
            ProcessState.Zombie => "zomb",
            ProcessState.DiskSleep => "disk",
            ProcessState.Traced => "trace",
            _ => throw Undefined(state),
        };

        private static KeyPart Text(ProcessMemoryStatistic statistic) => statistic switch
        {
            ProcessMemoryStatistic.Sum => Default("sum"),
            ProcessMemoryStatistic.Maximum => "max",
            ProcessMemoryStatistic.Minimum => "min",
            _ => throw Undefined(statistic),
        };

        private static KeyPart Text(ProcessMemoryType memoryType) => memoryType switch
        {
            ProcessMemoryType.VirtualSize => Default("vsize"),
            ProcessMemoryType.ResidentSet => "rss",
            ProcessMemoryType.DataStackAndCode => "size",
            ProcessMemoryType.PeakVirtualSize => "peak",
            ProcessMemoryType.Swapped => "swap",
            ProcessMemoryType.SharedLibraries => "lib",
            ProcessMemoryType.Locked => "lck",
            ProcessMemoryType.Pinned => "pin",
            ProcessMemoryType.PeakResidentSet => "hwm",
            ProcessMemoryType.Data => "data",
            ProcessMemoryType.Stack => "stk",
            ProcessMemoryType.Code => "exe",
            ProcessMemoryType.PageTables => "pte",
            _ => throw Undefined(memoryType),
        };

        private static KeyPart Text(ProcessCpuTime time) => time switch
        {
            ProcessCpuTime.Total => Default("total"),
            ProcessCpuTime.User => "user",
            ProcessCpuTime.Privileged => "system",
            _ => throw Undefined(time),
        };

        private static KeyPart Text(ProcessInfoCounter counter) => counter switch
        {
            ProcessInfoCounter.VirtualSizeKilobytes => Default("vmsize"),
            ProcessInfoCounter.WorkingSetKilobytes => "wkset",
            ProcessInfoCounter.PageFaults => "pf",
            ProcessInfoCounter.KernelTimeMilliseconds => "ktime",
            ProcessInfoCounter.UserTimeMilliseconds => "utime",
            ProcessInfoCounter.GdiObjects => "gdiobj",
            ProcessInfoCounter.UserObjects => "userobj",
            ProcessInfoCounter.IoReadBytes => "io_read_b",
            ProcessInfoCounter.IoReadOperations => "io_read_op",
            ProcessInfoCounter.IoWriteBytes => "io_write_b",
            ProcessInfoCounter.IoWriteOperations => "io_write_op",
            ProcessInfoCounter.IoOtherBytes => "io_other_b",
            ProcessInfoCounter.IoOtherOperations => "io_other_op",
            _ => throw Undefined(counter),
        };

        private static KeyPart Text(ProcessInfoStatistic statistic) => statistic switch
        {
            ProcessInfoStatistic.Average => Default("avg"),
            ProcessInfoStatistic.Minimum => "min",
            ProcessInfoStatistic.Maximum => "max",
            ProcessInfoStatistic.Sum => "sum",
            _ => throw Undefined(statistic),
        };
    }

    /// <summary>The kernel's file and process limits.</summary>
    public static class Kernel
    {
        /// <summary>The most files the kernel will hold open at once: <c>kernel.maxfiles</c>.</summary>
        /// <remarks>Linux and macOS only.</remarks>
        public static ZabbixItemKey<ulong> MaxFiles { get; } = Item<ulong>("kernel.maxfiles");

        /// <summary>The most processes the kernel will run at once: <c>kernel.maxproc</c>.</summary>
        /// <remarks>Linux and macOS only.</remarks>
        public static ZabbixItemKey<ulong> MaxProcesses { get; } = Item<ulong>("kernel.maxproc");

        /// <summary>How many file descriptors are open now: <c>kernel.openfiles</c>.</summary>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> OpenFiles { get; } = Item<ulong>("kernel.openfiles");
    }

    /// <summary>The network as an agent sees it: its interfaces and sockets, and whether the services and names it can reach answer.</summary>
    public static class Network
    {
        /// <summary>A service an agent can check over TCP by speaking its protocol's greeting.</summary>
        public enum TcpService
        {
            /// <summary>SSH, on port 22 unless told otherwise.</summary>
            Ssh,

            /// <summary>LDAP, on port 389 unless told otherwise. The SDK's own agents do not answer this check.</summary>
            Ldap,

            /// <summary>SMTP, on port 25 unless told otherwise.</summary>
            Smtp,

            /// <summary>FTP, on port 21 unless told otherwise.</summary>
            Ftp,

            /// <summary>HTTP, on port 80 unless told otherwise.</summary>
            Http,

            /// <summary>HTTPS, on port 443 unless told otherwise.</summary>
            Https,

            /// <summary>POP, on port 110 unless told otherwise.</summary>
            Pop,

            /// <summary>NNTP, on port 119 unless told otherwise.</summary>
            Nntp,

            /// <summary>IMAP, on port 143 unless told otherwise.</summary>
            Imap,

            /// <summary>Telnet, on port 23 unless told otherwise.</summary>
            Telnet,

            /// <summary>Whether a TCP connection can be made at all, on a port that must be given.</summary>
            Tcp,
        }

        /// <summary>Which UDP sockets to count.</summary>
        public enum UdpSocketState
        {
            /// <summary>Every UDP socket.</summary>
            Any,

            /// <summary>Only sockets connected to a remote end.</summary>
            Established,

            /// <summary>Only sockets with no remote end.</summary>
            Unconnected,
        }

        /// <summary>The DNS record types an agent can ask for.</summary>
        public enum DnsRecordType
        {
            /// <summary>The start of a zone's authority (<c>SOA</c>), which the key asks for when not told otherwise.</summary>
            StartOfAuthority,

            /// <summary>Every record the server will give (<c>ANY</c>).</summary>
            Any,

            /// <summary>An IPv4 address (<c>A</c>).</summary>
            Address,

            /// <summary>An IPv6 address (<c>AAAA</c>).</summary>
            Ipv6Address,

            /// <summary>An authoritative name server (<c>NS</c>).</summary>
            NameServer,

            /// <summary>A name this one is an alias of (<c>CNAME</c>).</summary>
            CanonicalName,

            /// <summary>A mailbox's domain (<c>MB</c>).</summary>
            Mailbox,

            /// <summary>A mail group member (<c>MG</c>).</summary>
            MailGroup,

            /// <summary>A mail rename (<c>MR</c>).</summary>
            MailRename,

            /// <summary>A mail destination (<c>MD</c>).</summary>
            MailDestination,

            /// <summary>A mail forwarder (<c>MF</c>).</summary>
            MailForwarder,

            /// <summary>An empty record (<c>NULL</c>).</summary>
            Null,

            /// <summary>A well-known service (<c>WKS</c>).</summary>
            WellKnownService,

            /// <summary>The name of an address (<c>PTR</c>); the key's name is then the address.</summary>
            ReversePointer,

            /// <summary>Host information (<c>HINFO</c>).</summary>
            HostInformation,

            /// <summary>Mailbox or mail list information (<c>MINFO</c>).</summary>
            MailInformation,

            /// <summary>A mail exchange (<c>MX</c>).</summary>
            MailExchange,

            /// <summary>Free text (<c>TXT</c>).</summary>
            Text,

            /// <summary>A service's host and port (<c>SRV</c>).</summary>
            Service,
        }

        /// <summary>The network interfaces, as low-level discovery JSON: <c>net.if.discovery</c>.</summary>
        public static ZabbixItemKey<string> InterfaceDiscovery { get; } = Item<string>("net.if.discovery");

        /// <summary>A line of text for each network interface, with its type, state, address and description: <c>net.if.list</c>.</summary>
        /// <remarks>Windows only.</remarks>
        public static ZabbixItemKey<string> InterfaceList { get; } = Item<string>("net.if.list");

        /// <summary>The bytes an interface has received: <c>net.if.in[interface,bytes]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        public static ZabbixItemKey<ulong> BytesReceived(string interfaceName) => Item<ulong>("net.if.in", Required(interfaceName));

        /// <summary>The packets an interface has received: <c>net.if.in[interface,packets]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        public static ZabbixItemKey<ulong> PacketsReceived(string interfaceName) => Item<ulong>("net.if.in", Required(interfaceName), "packets");

        /// <summary>The errors an interface has had receiving: <c>net.if.in[interface,errors]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        public static ZabbixItemKey<ulong> ReceiveErrors(string interfaceName) => Item<ulong>("net.if.in", Required(interfaceName), "errors");

        /// <summary>The incoming packets an interface has dropped: <c>net.if.in[interface,dropped]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        /// <remarks>Linux and Windows only.</remarks>
        public static ZabbixItemKey<ulong> ReceiveDrops(string interfaceName) => Item<ulong>("net.if.in", Required(interfaceName), "dropped");

        /// <summary>The buffer overruns an interface has had receiving: <c>net.if.in[interface,overruns]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> ReceiveOverruns(string interfaceName) => Item<ulong>("net.if.in", Required(interfaceName), "overruns");

        /// <summary>The framing errors an interface has had receiving: <c>net.if.in[interface,frame]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> ReceiveFrameErrors(string interfaceName) => Item<ulong>("net.if.in", Required(interfaceName), "frame");

        /// <summary>The compressed packets an interface has received: <c>net.if.in[interface,compressed]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> CompressedReceived(string interfaceName) => Item<ulong>("net.if.in", Required(interfaceName), "compressed");

        /// <summary>The multicast frames an interface has received: <c>net.if.in[interface,multicast]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> MulticastReceived(string interfaceName) => Item<ulong>("net.if.in", Required(interfaceName), "multicast");

        /// <summary>The bytes an interface has sent: <c>net.if.out[interface,bytes]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        public static ZabbixItemKey<ulong> BytesSent(string interfaceName) => Item<ulong>("net.if.out", Required(interfaceName));

        /// <summary>The packets an interface has sent: <c>net.if.out[interface,packets]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        public static ZabbixItemKey<ulong> PacketsSent(string interfaceName) => Item<ulong>("net.if.out", Required(interfaceName), "packets");

        /// <summary>The errors an interface has had sending: <c>net.if.out[interface,errors]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        public static ZabbixItemKey<ulong> SendErrors(string interfaceName) => Item<ulong>("net.if.out", Required(interfaceName), "errors");

        /// <summary>The outgoing packets an interface has dropped: <c>net.if.out[interface,dropped]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        /// <remarks>Linux and Windows only.</remarks>
        public static ZabbixItemKey<ulong> SendDrops(string interfaceName) => Item<ulong>("net.if.out", Required(interfaceName), "dropped");

        /// <summary>The buffer overruns an interface has had sending: <c>net.if.out[interface,overruns]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> SendOverruns(string interfaceName) => Item<ulong>("net.if.out", Required(interfaceName), "overruns");

        /// <summary>The collisions an interface has detected sending: <c>net.if.out[interface,collisions]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> SendCollisions(string interfaceName) => Item<ulong>("net.if.out", Required(interfaceName), "collisions");

        /// <summary>The carrier losses an interface has detected sending: <c>net.if.out[interface,carrier]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> SendCarrierErrors(string interfaceName) => Item<ulong>("net.if.out", Required(interfaceName), "carrier");

        /// <summary>The compressed packets an interface has sent: <c>net.if.out[interface,compressed]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> CompressedSent(string interfaceName) => Item<ulong>("net.if.out", Required(interfaceName), "compressed");

        /// <summary>The bytes an interface has received and sent together: <c>net.if.total[interface,bytes]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        public static ZabbixItemKey<ulong> TotalBytes(string interfaceName) => Item<ulong>("net.if.total", Required(interfaceName));

        /// <summary>The packets an interface has received and sent together: <c>net.if.total[interface,packets]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        public static ZabbixItemKey<ulong> TotalPackets(string interfaceName) => Item<ulong>("net.if.total", Required(interfaceName), "packets");

        /// <summary>The errors an interface has had in both directions: <c>net.if.total[interface,errors]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        public static ZabbixItemKey<ulong> TotalErrors(string interfaceName) => Item<ulong>("net.if.total", Required(interfaceName), "errors");

        /// <summary>The packets an interface has dropped in both directions: <c>net.if.total[interface,dropped]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        /// <remarks>Linux and Windows only.</remarks>
        public static ZabbixItemKey<ulong> TotalDrops(string interfaceName) => Item<ulong>("net.if.total", Required(interfaceName), "dropped");

        /// <summary>The buffer overruns an interface has had in both directions: <c>net.if.total[interface,overruns]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> TotalOverruns(string interfaceName) => Item<ulong>("net.if.total", Required(interfaceName), "overruns");

        /// <summary>The compressed packets an interface has received and sent together: <c>net.if.total[interface,compressed]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> TotalCompressed(string interfaceName) => Item<ulong>("net.if.total", Required(interfaceName), "compressed");

        /// <summary>The collisions an interface has detected: <c>net.if.collisions[interface]</c>.</summary>
        /// <param name="interfaceName">The network interface's name.</param>
        /// <returns>The item for that interface.</returns>
        /// <remarks>Linux only.</remarks>
        public static ZabbixItemKey<ulong> Collisions(string interfaceName) => Item<ulong>("net.if.collisions", Required(interfaceName));

        /// <summary>The MAC addresses of the interfaces other than loopback: <c>system.hw.macaddr[interface,format]</c>.</summary>
        /// <param name="interfacePattern">A regular expression an interface's name must match; every interface when left out.</param>
        /// <param name="addressesOnly">Whether to answer the distinct addresses alone, without the names of their interfaces.</param>
        /// <returns>The item for those interfaces.</returns>
        public static ZabbixItemKey<string> MacAddresses(string? interfacePattern = null, bool addressesOnly = false)
            => Item<string>("system.hw.macaddr", interfacePattern, addressesOnly ? "short" : string.Empty);

        /// <summary>Whether anything listens on a TCP port on this machine: <c>net.tcp.listen[port]</c>.</summary>
        /// <param name="port">The TCP port to look for.</param>
        /// <returns>The item for that port.</returns>
        public static ZabbixItemKey<bool> IsTcpListening(int port) => Item<bool>("net.tcp.listen", Port(port));

        /// <summary>Whether anything listens on a UDP port on this machine: <c>net.udp.listen[port]</c>.</summary>
        /// <param name="port">The UDP port to look for.</param>
        /// <returns>The item for that port.</returns>
        public static ZabbixItemKey<bool> IsUdpListening(int port) => Item<bool>("net.udp.listen", Port(port));

        /// <summary>How many TCP sockets match: <c>net.tcp.socket.count[local address,local port,remote address,remote port,state]</c>.</summary>
        /// <param name="localAddress">The local address or CIDR block to match; any address when left out.</param>
        /// <param name="localPort">The local port to match; any port when left out.</param>
        /// <param name="remoteAddress">The remote address or CIDR block to match; any address when left out.</param>
        /// <param name="remotePort">The remote port to match; any port when left out.</param>
        /// <param name="state">The state a socket must be in; any state when left out.</param>
        /// <returns>The item for those sockets.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is a state Zabbix has no name for.</exception>
        /// <remarks>Linux and Windows only.</remarks>
        public static ZabbixItemKey<long> TcpSocketCount(string? localAddress = null, int? localPort = null, string? remoteAddress = null, int? remotePort = null, TcpState? state = null)
            => Item<long>("net.tcp.socket.count", localAddress, Port(localPort), remoteAddress, Port(remotePort), Text(state));

        /// <summary>
        /// How many UDP sockets match: <c>net.udp.socket.count[local address,local port,remote address,remote port,state]</c>. The SDK's
        /// own agents list a socket by its local end alone, so they refuse a remote end and connected sockets.
        /// </summary>
        /// <param name="localAddress">The local address or CIDR block to match; any address when left out.</param>
        /// <param name="localPort">The local port to match; any port when left out.</param>
        /// <param name="remoteAddress">The remote address or CIDR block to match; any address when left out.</param>
        /// <param name="remotePort">The remote port to match; any port when left out.</param>
        /// <param name="state">Whether a socket must be connected to count.</param>
        /// <returns>The item for those sockets.</returns>
        /// <remarks>Linux and Windows only.</remarks>
        public static ZabbixItemKey<long> UdpSocketCount(string? localAddress = null, int? localPort = null, string? remoteAddress = null, int? remotePort = null, UdpSocketState state = UdpSocketState.Any)
            => Item<long>("net.udp.socket.count", localAddress, Port(localPort), remoteAddress, Port(remotePort), Text(state));

        /// <summary>Whether a TCP connection to a port can be made: <c>net.tcp.port[address,port]</c>.</summary>
        /// <param name="port">The TCP port to connect to.</param>
        /// <param name="address">The address to connect to; 127.0.0.1 when left out.</param>
        /// <returns>The item for that port.</returns>
        public static ZabbixItemKey<bool> IsTcpPortOpen(int port, string? address = null) => Item<bool>("net.tcp.port", address, Port(port));

        /// <summary>Whether a service answers over TCP: <c>net.tcp.service[service,address,port]</c>.</summary>
        /// <param name="service">The service to check.</param>
        /// <param name="address">The address to connect to; 127.0.0.1 when left out.</param>
        /// <param name="port">The TCP port to connect to; the service's usual port when left out.</param>
        /// <returns>The item for that service.</returns>
        /// <exception cref="ArgumentException"><paramref name="service"/> is <see cref="TcpService.Tcp"/> and no port is given.</exception>
        public static ZabbixItemKey<bool> IsTcpServiceUp(TcpService service, string? address = null, int? port = null)
            => ServiceItem<bool>("net.tcp.service", service, address, port);

        /// <summary>
        /// How long a service takes to answer over TCP, or zero when it doesn't answer:
        /// <c>net.tcp.service.perf[service,address,port]</c>.
        /// </summary>
        /// <param name="service">The service to check.</param>
        /// <param name="address">The address to connect to; 127.0.0.1 when left out.</param>
        /// <param name="port">The TCP port to connect to; the service's usual port when left out.</param>
        /// <returns>The item for that service.</returns>
        /// <exception cref="ArgumentException"><paramref name="service"/> is <see cref="TcpService.Tcp"/> and no port is given.</exception>
        public static ZabbixItemKey<TimeSpan> TcpServiceResponseTime(TcpService service, string? address = null, int? port = null)
            => ServiceItem<TimeSpan>("net.tcp.service.perf", service, address, port);

        /// <summary>Whether an NTP server answers: <c>net.udp.service[ntp,address,port]</c>.</summary>
        /// <param name="address">The address to ask; 127.0.0.1 when left out.</param>
        /// <param name="port">The UDP port to ask on; 123 when left out.</param>
        /// <returns>The item for that server.</returns>
        public static ZabbixItemKey<bool> IsNtpUp(string? address = null, int? port = null) => Item<bool>("net.udp.service", "ntp", address, Port(port));

        /// <summary>How long an NTP server takes to answer, or zero when it doesn't answer: <c>net.udp.service.perf[ntp,address,port]</c>.</summary>
        /// <param name="address">The address to ask; 127.0.0.1 when left out.</param>
        /// <param name="port">The UDP port to ask on; 123 when left out.</param>
        /// <returns>The item for that server.</returns>
        public static ZabbixItemKey<TimeSpan> NtpResponseTime(string? address = null, int? port = null) => Item<TimeSpan>("net.udp.service.perf", "ntp", address, Port(port));

        /// <summary>Whether a DNS query gets an answer with records in it: <c>net.dns[server,name,type,timeout,count,protocol]</c>.</summary>
        /// <param name="name">The name to ask about.</param>
        /// <param name="server">The address of the DNS server to ask; the system's own servers when left out.</param>
        /// <param name="recordType">The type of record to ask for.</param>
        /// <param name="timeout">How long to wait for each try, in whole seconds of at least one.</param>
        /// <param name="attempts">How many times to try; at least one.</param>
        /// <param name="useTcp">Whether to ask over TCP rather than UDP.</param>
        /// <returns>The item for that query.</returns>
        public static ZabbixItemKey<bool> DnsResolves(string name, string? server = null, DnsRecordType recordType = DnsRecordType.StartOfAuthority, TimeSpan? timeout = null, int? attempts = null, bool useTcp = false)
            => DnsItem<bool>("net.dns", name, server, recordType, timeout, attempts, useTcp);

        /// <summary>How long a DNS query takes, or zero when no answer comes: <c>net.dns.perf[server,name,type,timeout,count,protocol]</c>.</summary>
        /// <param name="name">The name to ask about.</param>
        /// <param name="server">The address of the DNS server to ask; the system's own servers when left out.</param>
        /// <param name="recordType">The type of record to ask for.</param>
        /// <param name="timeout">How long to wait for each try, in whole seconds of at least one.</param>
        /// <param name="attempts">How many times to try; at least one.</param>
        /// <param name="useTcp">Whether to ask over TCP rather than UDP.</param>
        /// <returns>The item for that query.</returns>
        public static ZabbixItemKey<TimeSpan> DnsResponseTime(string name, string? server = null, DnsRecordType recordType = DnsRecordType.StartOfAuthority, TimeSpan? timeout = null, int? attempts = null, bool useTcp = false)
            => DnsItem<TimeSpan>("net.dns.perf", name, server, recordType, timeout, attempts, useTcp);

        /// <summary>The records a DNS query answers, as text: <c>net.dns.record[server,name,type,timeout,count,protocol]</c>.</summary>
        /// <param name="name">The name to ask about.</param>
        /// <param name="server">The address of the DNS server to ask; the system's own servers when left out.</param>
        /// <param name="recordType">The type of record to ask for.</param>
        /// <param name="timeout">How long to wait for each try, in whole seconds of at least one.</param>
        /// <param name="attempts">How many times to try; at least one.</param>
        /// <param name="useTcp">Whether to ask over TCP rather than UDP.</param>
        /// <returns>The item for that query.</returns>
        public static ZabbixItemKey<string> DnsRecords(string name, string? server = null, DnsRecordType recordType = DnsRecordType.StartOfAuthority, TimeSpan? timeout = null, int? attempts = null, bool useTcp = false)
            => DnsItem<string>("net.dns.record", name, server, recordType, timeout, attempts, useTcp);

        /// <summary>A Zabbix server's or proxy's internal statistics, as JSON: <c>zabbix.stats[address,port]</c>.</summary>
        /// <param name="address">The address of the server or proxy; 127.0.0.1 when left out.</param>
        /// <param name="port">The port it listens on; 10051 when left out.</param>
        /// <returns>The item for that server or proxy.</returns>
        public static ZabbixItemKey<string> ServerStatistics(string? address = null, int? port = null) => Item<string>("zabbix.stats", address, Port(port));

        /// <summary>
        /// How many items a Zabbix server or proxy has waiting, as JSON holding the count: <c>zabbix.stats[address,port,queue,from,to]</c>.
        /// </summary>
        /// <param name="address">The address of the server or proxy; 127.0.0.1 when left out.</param>
        /// <param name="port">The port it listens on; 10051 when left out.</param>
        /// <param name="minDelay">The shortest delay that counts, in whole seconds.</param>
        /// <param name="maxDelay">The longest delay that counts, in whole seconds.</param>
        /// <returns>The item for that server or proxy.</returns>
        public static ZabbixItemKey<string> ServerQueue(string? address = null, int? port = null, TimeSpan? minDelay = null, TimeSpan? maxDelay = null)
            => Item<string>("zabbix.stats", address, Port(port), "queue", Seconds(minDelay, 0), Seconds(maxDelay, 0));

        private static ZabbixItemKey<T> ServiceItem<T>(string key, TcpService service, string? address, int? port)
            where T : ISpanParsable<T>
        {
            if (service == TcpService.Tcp && port is null) throw new ArgumentException("The plain TCP check has no usual port, so it needs one.", nameof(port));
            return Item<T>(key, Text(service), address, Port(port));
        }

        private static ZabbixItemKey<T> DnsItem<T>(string key, string name, string? server, DnsRecordType recordType, TimeSpan? timeout, int? attempts, bool useTcp)
            where T : ISpanParsable<T>
            => Item<T>(key, server, Required(name), Text(recordType), Seconds(timeout, 1), Whole(attempts, 1), useTcp ? "tcp" : string.Empty);

        private static string? Text(TcpState? state) => state switch
        {
            null => null,
            TcpState.Established => "established",
            TcpState.SynSent => "syn_sent",
            TcpState.SynReceived => "syn_recv",
            TcpState.FinWait1 => "fin_wait1",
            TcpState.FinWait2 => "fin_wait2",
            TcpState.TimeWait => "time_wait",
            TcpState.Closed => "close",
            TcpState.CloseWait => "close_wait",
            TcpState.LastAck => "last_ack",
            TcpState.Listen => "listen",
            TcpState.Closing => "closing",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Zabbix has no name for this TCP state."),
        };

        private static string Text(UdpSocketState state) => state switch
        {
            UdpSocketState.Any => string.Empty,
            UdpSocketState.Established => "established",
            UdpSocketState.Unconnected => "unconn",
            _ => throw Undefined(state),
        };

        private static string Text(TcpService service) => service switch
        {
            TcpService.Ssh => "ssh",
            TcpService.Ldap => "ldap",
            TcpService.Smtp => "smtp",
            TcpService.Ftp => "ftp",
            TcpService.Http => "http",
            TcpService.Https => "https",
            TcpService.Pop => "pop",
            TcpService.Nntp => "nntp",
            TcpService.Imap => "imap",
            TcpService.Telnet => "telnet",
            TcpService.Tcp => "tcp",
            _ => throw Undefined(service),
        };

        private static KeyPart Text(DnsRecordType recordType) => recordType switch
        {
            DnsRecordType.StartOfAuthority => Default("SOA"),
            DnsRecordType.Any => "ANY",
            DnsRecordType.Address => "A",
            DnsRecordType.Ipv6Address => "AAAA",
            DnsRecordType.NameServer => "NS",
            DnsRecordType.CanonicalName => "CNAME",
            DnsRecordType.Mailbox => "MB",
            DnsRecordType.MailGroup => "MG",
            DnsRecordType.MailRename => "MR",
            DnsRecordType.MailDestination => "MD",
            DnsRecordType.MailForwarder => "MF",
            DnsRecordType.Null => "NULL",
            DnsRecordType.WellKnownService => "WKS",
            DnsRecordType.ReversePointer => "PTR",
            DnsRecordType.HostInformation => "HINFO",
            DnsRecordType.MailInformation => "MINFO",
            DnsRecordType.MailExchange => "MX",
            DnsRecordType.Text => "TXT",
            DnsRecordType.Service => "SRV",
            _ => throw Undefined(recordType),
        };
    }

    /// <summary>Web pages and TLS certificates, fetched by the agent.</summary>
    [SuppressMessage("Naming", "CA1724:Type names should not match namespaces",
        Justification = "A nested group, only ever written ZabbixItemKeys.Web, so it can't be mistaken for the namespace System.Web, which this library doesn't reference.")]
    public static class Web
    {
        /// <summary>A web page's status line and headers, then its body: <c>web.page.get[host,path,port]</c>.</summary>
        /// <param name="host">The host to fetch from, or a whole address starting <c>http://</c> or <c>https://</c>.</param>
        /// <param name="path">The page's path, left out when <paramref name="host"/> is a whole address.</param>
        /// <param name="port">The port to connect to, left out when <paramref name="host"/> is a whole address; 80 otherwise.</param>
        /// <returns>The item for that page.</returns>
        public static ZabbixItemKey<string> Page(string host, string? path = null, int? port = null)
            => Item<string>("web.page.get", Required(host), path, Port(port));

        /// <summary>How long a web page takes to load: <c>web.page.perf[host,path,port]</c>.</summary>
        /// <param name="host">The host to fetch from, or a whole address starting <c>http://</c> or <c>https://</c>.</param>
        /// <param name="path">The page's path, left out when <paramref name="host"/> is a whole address.</param>
        /// <param name="port">The port to connect to, left out when <paramref name="host"/> is a whole address; 80 otherwise.</param>
        /// <returns>The item for that page.</returns>
        public static ZabbixItemKey<TimeSpan> PageLoadTime(string host, string? path = null, int? port = null)
            => Item<TimeSpan>("web.page.perf", Required(host), path, Port(port));

        /// <summary>
        /// The first line of a web page that matches a regular expression, or empty text when none does:
        /// <c>web.page.regexp[host,path,port,pattern,length,output]</c>.
        /// </summary>
        /// <param name="host">The host to fetch from, or a whole address starting <c>http://</c> or <c>https://</c>.</param>
        /// <param name="pattern">The regular expression a line must match.</param>
        /// <param name="path">The page's path, left out when <paramref name="host"/> is a whole address.</param>
        /// <param name="port">The port to connect to, left out when <paramref name="host"/> is a whole address; 80 otherwise.</param>
        /// <param name="length">The most characters to answer; every character when left out.</param>
        /// <param name="output">What to answer instead of the line, where <c>\0</c> is the match and <c>\1</c> to <c>\9</c> its groups.</param>
        /// <returns>The item for that page.</returns>
        public static ZabbixItemKey<string> PageMatch(string host, string pattern, string? path = null, int? port = null, int? length = null, string? output = null)
            => Item<string>("web.page.regexp", Required(host), path, Port(port), Required(pattern), Whole(length, 1), output);

        /// <summary>A site's TLS certificate and whether it is valid, as JSON: <c>web.certificate.get[host name,port,address]</c>.</summary>
        /// <param name="hostname">The host name the certificate must be good for.</param>
        /// <param name="port">The port to connect to; 443 when left out.</param>
        /// <param name="address">The IPv4 address or name to connect to; the host name when left out.</param>
        /// <returns>The item for that site.</returns>
        public static ZabbixItemKey<string> Certificate(string hostname, int? port = null, string? address = null)
            => Item<string>("web.certificate.get", Required(hostname), Port(port), address);
    }

    // One parameter of a key, and whether the key may leave it out when nothing after it is written. Absent or empty text may go,
    // and so may a mode at the key's default (Default), which keeps its own text when a later parameter holds its place open:
    // system.cpu.load for the two defaults, but system.cpu.load[all,avg5] rather than system.cpu.load[,avg5] for a later choice.
    private readonly record struct KeyPart(string Text, bool CanOmit)
    {
        public static implicit operator KeyPart(string? text) => new(text ?? string.Empty, string.IsNullOrEmpty(text));
    }

    private static KeyPart Default(string text) => new(text, CanOmit: true);

    private static ZabbixItemKey<T> Item<T>(string name, params ReadOnlySpan<KeyPart> parameters)
        where T : ISpanParsable<T>
    {
        int count = parameters.Length;
        while (count > 0 && parameters[count - 1].CanOmit) count--;
        string[] text = new string[count];
        for (int i = 0; i < count; i++) text[i] = parameters[i].Text;
        return new ZabbixItemKey<T>(ZabbixItemKey.Create(name, text));
    }

    private static KeyPart Window(SampleWindow window) => window switch
    {
        SampleWindow.OneMinute => Default("avg1"),
        SampleWindow.FiveMinutes => "avg5",
        SampleWindow.FifteenMinutes => "avg15",
        _ => throw Undefined(window),
    };

    private static string Required(string value, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);
        return value;
    }

    private static string? Whole(long? value, long minimum, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        if (value is not { } number) return null;
        ArgumentOutOfRangeException.ThrowIfLessThan(number, minimum, parameterName);
        return number.ToString(CultureInfo.InvariantCulture);
    }

    private static string? Seconds(TimeSpan? value, long minimum, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        => value is { } time ? Whole((long)time.TotalSeconds, minimum, parameterName) : null;

    private static string? Port(int? port, [CallerArgumentExpression(nameof(port))] string? parameterName = null)
    {
        if (port is not { } number) return null;
        ArgumentOutOfRangeException.ThrowIfNegative(number, parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, ushort.MaxValue, parameterName);
        return number.ToString(CultureInfo.InvariantCulture);
    }

    private static ArgumentOutOfRangeException Undefined<TEnum>(TEnum value, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where TEnum : struct, Enum
        => new(parameterName, value, "The value is not one the enumeration defines.");
}
