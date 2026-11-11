// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Community.Zabbix.Items;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The bundled keys that report over time (docs/adr/0009-bundled-items.md §5): <c>system.cpu.util</c>,
/// <c>proc.cpu.util</c> and the per-second <c>vfs.dev</c> types. A fake clock steps the collector one second at a time
/// and fixture files stand in for <c>/proc</c>, so each answer is an exact figure. The collector runs only while a key
/// asks, and does not outlive its owner.
/// </summary>
public sealed class ItemCollectorTests : IDisposable
{
    private static readonly TimeSpan s_second = TimeSpan.FromSeconds(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"zbx-collector-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new() { AutoAdvanceAmount = TimeSpan.Zero };
    private readonly ZabbixHostFiles _files;
    private readonly ZabbixItemCollector _collector;
    private readonly ZabbixSystemItems _system;
    private readonly ZabbixProcessItems _processes;
    private readonly ZabbixFileSystemItems _devices;

    public ItemCollectorTests()
    {
        _files = new ZabbixHostFiles(_root, linux: true);
        _collector = new ZabbixItemCollector(_files, _time);
        _system = new ZabbixSystemItems(_collector);
        _processes = new ZabbixProcessItems(_collector);
        _devices = new ZabbixFileSystemItems(_collector);
    }

    public void Dispose()
    {
        _system.Dispose();
        _processes.Dispose();
        _devices.Dispose();
        _collector.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- system.cpu.util ----

    [Fact]
    public async Task The_first_request_waits_for_the_second_sample_and_answers_from_the_difference()
    {
        Stat("cpu  100 0 100 800 0 0 0 0 0 0");

        Task<ZabbixAgentAnswer> asked = _system.GetValueAsync(Key("system.cpu.util"), Ct).AsTask();

        Assert.False(asked.IsCompleted);
        Assert.True(_collector.IsSampling);
        Stat("cpu  150 0 100 850 0 0 0 0 0 0");
        _time.Advance(s_second);
        Assert.Equal("50.000000", (await asked).Value);
    }

    [Fact]
    public async Task A_window_is_the_samples_it_covers_and_the_series_holds_fifteen_minutes()
    {
        ulong user = 0, idle = 0;
        void Tick(int seconds, ulong userPerSecond, ulong idlePerSecond)
        {
            for (int i = 0; i < seconds; i++)
            {
                user += userPerSecond;
                idle += idlePerSecond;
                Stat($"cpu  {user} 0 0 {idle} 0 0 0 0 0 0");
                _time.Advance(s_second);
            }
        }

        Stat("cpu  0 0 0 0 0 0 0 0 0 0");
        Task<ZabbixAgentAnswer> first = _system.GetValueAsync(Key("system.cpu.util[all,user]"), Ct).AsTask();
        Tick(60, 0, 100);                                                                // a minute idle
        await first;
        Tick(60, 100, 0);                                                                // a minute busy

        Assert.Equal("100.000000", await ValueAsync(_system, "system.cpu.util[all,user,avg1]"));
        Assert.Equal("50.000000", await ValueAsync(_system, "system.cpu.util[all,user,avg5]"));   // the two minutes it has
        Assert.Equal("50.000000", await ValueAsync(_system, "system.cpu.util[all,idle,avg15]"));

        Tick(1000, 25, 75);                                                              // past the fifteen minutes it keeps

        Assert.Equal("25.000000", await ValueAsync(_system, "system.cpu.util[all,user,avg15]"));
    }

    [Theory]
    [InlineData("system.cpu.util[,user]", "20.000000")]                                  // user without its guest time
    [InlineData("system.cpu.util[,nice]", "10.000000")]
    [InlineData("system.cpu.util[,system]", "10.000000")]
    [InlineData("system.cpu.util[,idle]", "10.000000")]
    [InlineData("system.cpu.util[,iowait]", "10.000000")]
    [InlineData("system.cpu.util[,interrupt]", "5.000000")]
    [InlineData("system.cpu.util[,softirq]", "5.000000")]
    [InlineData("system.cpu.util[,steal]", "10.000000")]
    [InlineData("system.cpu.util[,guest]", "10.000000")]
    [InlineData("system.cpu.util[,guest_nice]", "10.000000")]
    public async Task Each_state_is_its_share_of_the_time_with_guest_time_apart(string key, string value)
    {
        Stat("cpu  0 0 0 0 0 0 0 0 0 0");
        Task<ZabbixAgentAnswer> asked = _system.GetValueAsync(Key(key), Ct).AsTask();
        Stat("cpu  30 20 10 10 10 5 5 10 10 10");                                        // user and nice include the guests'
        _time.Advance(s_second);

        Assert.Equal(value, (await asked).Value);
    }

    [Fact]
    public async Task A_processor_is_its_own_series_and_one_that_is_not_there_is_refused_at_once()
    {
        Stat("cpu  0 0 0 0 0 0 0 0 0 0\ncpu0 0 0 0 0 0 0 0 0 0 0\ncpu1 0 0 0 0 0 0 0 0 0 0");
        Task<ZabbixAgentAnswer> asked = _system.GetValueAsync(Key("system.cpu.util[1,user]"), Ct).AsTask();
        Stat("cpu  50 0 0 50 0 0 0 0 0 0\ncpu0 0 0 0 50 0 0 0 0 0 0\ncpu1 50 0 0 0 0 0 0 0 0 0");
        _time.Advance(s_second);

        Assert.Equal("100.000000", (await asked).Value);
        Assert.Equal("The processors' figures cannot be read.", await ErrorAsync(_system, "system.cpu.util[7]"));
    }

    [Fact]
    public async Task A_processor_that_goes_away_is_refused_until_it_returns()
    {
        Stat("cpu  0 0 0 0 0 0 0 0 0 0\ncpu1 0 0 0 0 0 0 0 0 0 0");
        Task<ZabbixAgentAnswer> asked = _system.GetValueAsync(Key("system.cpu.util[1,user]"), Ct).AsTask();
        Stat("cpu  10 0 0 0 0 0 0 0 0 0\ncpu1 10 0 0 0 0 0 0 0 0 0");
        _time.Advance(s_second);
        await asked;

        Stat("cpu  20 0 0 0 0 0 0 0 0 0");
        _time.Advance(s_second);
        Assert.Equal("The processors' figures cannot be read.", await ErrorAsync(_system, "system.cpu.util[1,user]"));

        Stat("cpu  30 0 0 0 0 0 0 0 0 0\ncpu1 30 0 0 0 0 0 0 0 0 0");
        _time.Advance(s_second);
        Assert.Equal("100.000000", await ValueAsync(_system, "system.cpu.util[1,user]"));
    }

    [Theory]
    [InlineData("system.cpu.util[all,nothing]", "The key's second parameter is not valid.")]
    [InlineData("system.cpu.util[all,user,avg2]", "The key's third parameter is not valid.")]
    [InlineData("system.cpu.util[many]", "The key's first parameter is not valid.")]
    [InlineData("system.cpu.util[all,user,avg1,logical]", "The key has more parameters than it takes.")]
    public async Task A_parameter_is_refused_before_anything_is_sampled(string key, string error)
    {
        Stat("cpu  0 0 0 0 0 0 0 0 0 0");

        Assert.Equal(error, await ErrorAsync(_system, key));
        Assert.False(_collector.IsSampling);
    }

    // ---- proc.cpu.util ----

    [Fact]
    public async Task A_process_query_s_use_is_a_percentage_of_one_processor_to_one_decimal()
    {
        Process(42, "nginx", uid: 33, user: 0, system: 0);
        Process(43, "nginx", uid: 33, user: 0, system: 0);
        Process(50, "other", uid: 0, user: 0, system: 0);
        Write("/etc/passwd", "root:x:0:0::/root:/bin/sh\nwww-data:x:33:33::/var/www:/bin/sh\n");

        Task<ZabbixAgentAnswer> total = _processes.GetValueAsync(Key("proc.cpu.util[nginx]"), Ct).AsTask();
        Task<ZabbixAgentAnswer> user = _processes.GetValueAsync(Key("proc.cpu.util[nginx,,user]"), Ct).AsTask();
        Task<ZabbixAgentAnswer> system = _processes.GetValueAsync(Key("proc.cpu.util[nginx,,system]"), Ct).AsTask();
        Task<ZabbixAgentAnswer> byUser = _processes.GetValueAsync(Key("proc.cpu.util[,www-data]"), Ct).AsTask();
        Task<ZabbixAgentAnswer> nobody = _processes.GetValueAsync(Key("proc.cpu.util[,nobody-at-all]"), Ct).AsTask();
        Process(42, "nginx", uid: 33, user: 75, system: 25);                             // one processor's full second
        Process(43, "nginx", uid: 33, user: 33, system: 0);
        Process(50, "other", uid: 0, user: 100, system: 0);
        _time.Advance(s_second);

        Assert.Equal("133.000000", (await total).Value);
        Assert.Equal("108.000000", (await user).Value);
        Assert.Equal("25.000000", (await system).Value);
        Assert.Equal("133.000000", (await byUser).Value);
        Assert.Equal("0.000000", (await nobody).Value);
    }

    [Fact]
    public async Task A_process_that_starts_or_restarts_between_samples_does_not_jump_the_total()
    {
        Process(42, "nginx", uid: 33, user: 1000, system: 0, start: 500);

        Task<ZabbixAgentAnswer> asked = _processes.GetValueAsync(Key("proc.cpu.util[nginx]"), Ct).AsTask();
        Process(42, "nginx", uid: 33, user: 5, system: 0, start: 900);                   // the pid again, a new process
        Process(43, "nginx", uid: 33, user: 90000, system: 0, start: 901);               // new since the last sample
        _time.Advance(s_second);

        Assert.Equal("0.000000", (await asked).Value);
    }

    [Fact]
    public async Task A_process_query_follows_the_command_line()
    {
        Process(42, "python", uid: 0, user: 0, system: 0, cmdline: "python\0worker.py\0");
        Process(43, "python", uid: 0, user: 0, system: 0, cmdline: "python\0web.py\0");

        Task<ZabbixAgentAnswer> asked = _processes.GetValueAsync(Key("proc.cpu.util[python,,,worker]"), Ct).AsTask();
        Process(42, "python", uid: 0, user: 50, system: 0, cmdline: "python\0worker.py\0");
        Process(43, "python", uid: 0, user: 25, system: 0, cmdline: "python\0web.py\0");
        _time.Advance(s_second);

        Assert.Equal("50.000000", (await asked).Value);
        Assert.StartsWith("The fourth parameter is not a valid regular expression", await ErrorAsync(_processes, "proc.cpu.util[python,,,\"[oops\"]"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Too_many_process_queries_are_refused_as_Zabbix_refuses_them()
    {
        Directory.CreateDirectory(_files.Resolve("/proc"));
        for (int i = 0; i < 1024; i++) _ = _processes.GetValueAsync(Key($"proc.cpu.util[name-{i}]"), Ct).AsTask();

        Assert.Equal("The agent already follows as many process queries as it can.", await ErrorAsync(_processes, "proc.cpu.util[one-more]"));
    }

    // ---- vfs.dev ----

    [Fact]
    public async Task A_block_device_s_rates_are_per_second_over_the_window()
    {
        Diskstats(sectorsRead: 0, reads: 0, sectorsWritten: 0, writes: 0);

        Task<ZabbixAgentAnswer> sps = _devices.GetValueAsync(Key("vfs.dev.read[sda]"), Ct).AsTask();
        Task<ZabbixAgentAnswer> ops = _devices.GetValueAsync(Key("vfs.dev.write[/dev/sda,ops,avg5]"), Ct).AsTask();
        Task<ZabbixAgentAnswer> all = _devices.GetValueAsync(Key("vfs.dev.read[all,sps]"), Ct).AsTask();
        Diskstats(sectorsRead: 1000, reads: 10, sectorsWritten: 4000, writes: 40);
        _time.Advance(s_second);

        Assert.Equal("1000.000000", (await sps).Value);
        Assert.Equal("40.000000", (await ops).Value);
        Assert.Equal("1000.000000", (await all).Value);                                  // sda's, its partition's did not move
    }

    [Fact]
    public async Task A_block_device_that_is_not_there_is_refused_at_once()
    {
        Diskstats(sectorsRead: 0, reads: 0, sectorsWritten: 0, writes: 0);

        Assert.Equal("The block device's counters cannot be read.", await ErrorAsync(_devices, "vfs.dev.read[sdz]"));
        Assert.Equal("The key's third parameter is not valid.", await ErrorAsync(_devices, "vfs.dev.read[sda,sps,avg2]"));
        Assert.False(_collector.IsSampling);
    }

    // ---- the collector's life ----

    [Fact]
    public async Task Sampling_stops_when_every_series_goes_unrequested_and_starts_again_when_asked()
    {
        Diskstats(sectorsRead: 0, reads: 0, sectorsWritten: 0, writes: 0);
        Task<ZabbixAgentAnswer> asked = _devices.GetValueAsync(Key("vfs.dev.read[sda]"), Ct).AsTask();
        _time.Advance(s_second);
        await asked;
        Assert.True(_collector.IsSampling);

        _time.Advance(TimeSpan.FromHours(3) + TimeSpan.FromSeconds(2));                  // Zabbix's DISKDEVICE_TTL

        Assert.False(_collector.IsSampling);

        asked = _devices.GetValueAsync(Key("vfs.dev.read[sda]"), Ct).AsTask();
        Assert.True(_collector.IsSampling);
        Diskstats(sectorsRead: 500, reads: 0, sectorsWritten: 0, writes: 0);
        _time.Advance(s_second);
        Assert.Equal("500.000000", (await asked).Value);
    }

    [Fact]
    public void A_disposed_collector_stops_sampling_and_does_not_start_again()
    {
        Diskstats(sectorsRead: 0, reads: 0, sectorsWritten: 0, writes: 0);
        _ = _devices.GetValueAsync(Key("vfs.dev.read[sda]"), Ct).AsTask();
        Assert.True(_collector.IsSampling);

        _collector.Dispose();
        Assert.False(_collector.IsSampling);

        _ = _devices.GetValueAsync(Key("vfs.dev.read[sda1]"), Ct).AsTask();
        Assert.False(_collector.IsSampling);
    }

    [Fact]
    public void A_table_nobody_holds_can_be_collected_though_its_timer_is_running()
    {
        Diskstats(sectorsRead: 0, reads: 0, sectorsWritten: 0, writes: 0);
        WeakReference<ZabbixItemCollector> collector = StartAndForget();

        for (int i = 0; i < 10 && IsAlive(collector); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(IsAlive(collector));
        _time.Advance(s_second);                                                         // the tick finds it gone, and stops the timer
    }

    // In a frame of its own: a debug build keeps a discarded out variable alive for the whole of the method it is in.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<ZabbixItemCollector> collector) => collector.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference<ZabbixItemCollector> StartAndForget()
    {
#pragma warning disable CA2000 // what this test is about: nobody disposes of them
        var collector = new ZabbixItemCollector(_files, _time);
        var items = new ZabbixFileSystemItems(collector);
#pragma warning restore CA2000
        _ = items.GetValueAsync(Key("vfs.dev.read[sda]"), CancellationToken.None).AsTask();   // an agent's token ends with the check
        Assert.True(collector.IsSampling);
        return new WeakReference<ZabbixItemCollector>(collector);
    }

    [Fact]
    public async Task An_agent_answers_a_sampled_key_and_stops_sampling_when_it_stops()
    {
        ZabbixItemCollector collector;
        using (IHost host = await TestHost.StartAsync(services => services.AddZabbixPassiveAgent("edge").Configure(options =>
        {
            options.Hostname = "edge-01";
            options.Address = "127.0.0.1";
            options.Port = 0;
            options.AllowedPeers.Add("127.0.0.1");
        }), Ct))
        {
            collector = host.Services.GetRequiredService<ZabbixItemCollector>();
            int port = ((IPEndPoint)host.Services.GetRequiredKeyedService<ZabbixPassiveAgent>("edge").Listener!.LocalEndPoint!).Port;
            using var getter = new ZabbixGetter($"127.0.0.1:{port}");

            Assert.Equal("0.000000", (await getter.GetAsync("proc.cpu.util[no-such-process-runs]", Ct)).Value);
            Assert.True(collector.IsSampling);
        }

        Assert.False(collector.IsSampling);
    }

    [Fact]
    public async Task A_table_without_a_container_answers_a_sampled_key_within_the_check_s_deadline()
    {
        ZabbixItemDictionary handlers = ZabbixItemDictionary.BuiltIn("edge-01");

        ZabbixAgentAnswer answer = await handlers.AnswerAsync(Key("proc.cpu.util[no-such-process-runs]"), TimeSpan.FromSeconds(5), Ct);

        Assert.Equal("0.000000", answer.Value);
    }

    private void Stat(string lines) => Write("/proc/stat", lines + "\nintr 0\nctxt 0\nbtime 1\n");

    private void Diskstats(ulong sectorsRead, ulong reads, ulong sectorsWritten, ulong writes)
        => Write("/proc/diskstats", $"   8       0 sda {reads} 0 {sectorsRead} 0 {writes} 0 {sectorsWritten} 0 0 0 0\n   8       1 sda1 1 0 7 0 1 0 7 0 0 0 0\n");

    private void Process(int pid, string name, int uid, int user, int system, int start = 100, string? cmdline = null)
    {
        Write($"/proc/{pid}/status", $"Name:\t{name}\nState:\tS (sleeping)\nPPid:\t1\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\nGid:\t{uid}\t{uid}\t{uid}\t{uid}\nVmRSS:\t1 kB\n");
        Write($"/proc/{pid}/cmdline", cmdline ?? name + "\0");
        Write($"/proc/{pid}/stat", $"{pid} ({name}) S 1 0 0 0 0 0 0 0 0 0 {user} {system} 0 0 20 0 1 0 {start}\n");
    }

    private void Write(string path, string text)
    {
        string resolved = _files.Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        File.WriteAllText(resolved, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static ZabbixItemKey Key(string text) => ZabbixItemKey.Parse(text, null);

    private static async Task<string> ValueAsync(ZabbixItemProvider items, string key)
    {
        ZabbixAgentAnswer answer = await items.GetValueAsync(Key(key), Ct);
        Assert.True(answer.IsSupported, $"{key}: {answer.Error}");
        return answer.Value!;
    }

    private static async Task<string?> ErrorAsync(ZabbixItemProvider items, string key)
    {
        ZabbixAgentAnswer answer = await items.GetValueAsync(Key(key), Ct);
        Assert.False(answer.IsSupported, $"{key} answered {answer.Value}");
        return answer.Error;
    }
}
