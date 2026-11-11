// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Proxy;
using Community.Zabbix.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The proxy protocol's pieces the loops stand on: the version as the server parses and compares it, the
/// <c>more</c>/<c>proxy_delay</c> of what a page leaves behind and the offline buffer, and the one fault contract.
/// </summary>
public sealed class ProxyProtocolTests
{
    [Theory]
    [InlineData("7.0.31", 7, 0, 31)]
    [InlineData("8.0.0rc1", 8, 0, 0)]
    [InlineData("7.4", 7, 4, 0)]
    [InlineData("6.4.0alpha1", 6, 4, 0)]
    public void A_version_is_read_as_the_server_reads_it(string text, int major, int minor, int patch)
    {
        Assert.True(ZabbixComponentVersion.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out ZabbixComponentVersion version));
        Assert.Equal(new ZabbixComponentVersion(major, minor, patch), version);
        Assert.Equal(new Version(major, minor, patch), version.ToVersion());
    }

    [Fact]
    public void A_component_version_orders_as_upstreams_packed_integer_and_formats_as_the_protocol_writes_it()
    {
        ZabbixComponentVersion rc = ZabbixComponentVersion.Parse("8.0.0rc1", System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(new ZabbixComponentVersion(4, 4) <= new ZabbixComponentVersion(7, 0, 31));
        Assert.True(new ZabbixComponentVersion(7, 4, 15) < rc);
        Assert.Equal(new ZabbixComponentVersion(7, 0), new ZabbixComponentVersion(7, 0, 31).WithoutPatch);
        Assert.Equal("8.0.0", rc.ToString());
        Assert.Equal(rc, ZabbixComponentVersion.FromVersion(new Version(8, 0)));
        Assert.Equal(["4.2.0", "7.0.31", "8.0.0"], new[] { "8.0.0", "4.2", "7.0.31" }.Select(text => ZabbixComponentVersion.Parse(text, System.Globalization.CultureInfo.InvariantCulture)).Order().Select(version => version.ToString()));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("8")]
    [InlineData("")]
    [InlineData("x.y")]                                                                   // atoi reads 0.0; refused here (a typo is an error)
    public void Text_that_is_not_a_version_is_refused(string text) => Assert.False(ZabbixComponentVersion.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out _));

    [Theory]
    [InlineData("7.0.31", "7.0.0", true)]
    [InlineData("7.0.31", "7.4.0", false)]
    [InlineData("8.0.0", "7.4.0", false)]
    public void A_server_serves_configuration_only_to_its_own_major_and_minor(string server, string proxy, bool served)
    {
        Assert.Equal(served, ZabbixProxyProtocol.ServesConfig(ZabbixComponentVersion.Parse(server, System.Globalization.CultureInfo.InvariantCulture), ZabbixComponentVersion.Parse(proxy, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task What_a_page_leaves_behind_is_more_and_its_age_since_stored_is_proxy_delay()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        await using var store = new ZabbixMemoryStore<ZabbixValue>(services: new ZabbixStoreServices { Time = time });
        store.TryWrite(new ZabbixValue("h", "k", "1", DateTimeOffset.UnixEpoch));
        store.TryWrite(new ZabbixValue("h", "k", "2", DateTimeOffset.UnixEpoch));
        time.Advance(TimeSpan.FromSeconds(42));
        Assert.True(store.TryLease(out _));

        (bool more, long delay) = ZabbixProxyPaging.Behind(store, time);
        Assert.True(store.TryLease(out _));
        (bool after, long none) = ZabbixProxyPaging.Behind(store, time);

        Assert.Equal((true, 42L), (more, delay));
        Assert.Equal((false, 0L), (after, none));
    }

    [Fact]
    public void The_offline_buffer_is_the_store_s_age_limit()
    {
        var options = new ZabbixProxyOptions { OfflineBuffer = TimeSpan.FromHours(6), MaxItems = 500 };

        ZabbixStorePolicy<ZabbixValue> policy = options.StoragePolicy(_ => true);

        Assert.Equal(TimeSpan.FromHours(6), policy.MaxAge);
        Assert.Equal(500, policy.MaxItems);
    }

    [Fact]
    public async Task A_loop_failure_outside_the_contract_is_logged_as_an_error_and_faults_the_loop()
    {
        var logger = new FakeLogger();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ZabbixProxyPaging.RunAsync("upload", _ => throw new InvalidOperationException("a defect"), logger, "proxy-01", TestContext.Current.CancellationToken));

        FakeLogRecord record = Assert.Single(logger.Collector.GetSnapshot(), record => record.Id.Id == 7030);
        Assert.Equal(LogLevel.Error, record.Level);
    }

    [Fact]
    public async Task A_stopped_loop_ends_quietly()
    {
        var logger = new FakeLogger();
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await ZabbixProxyPaging.RunAsync("upload", token => Task.FromCanceled(token), logger, "proxy-01", stopping.Token);

        Assert.Empty(logger.Collector.GetSnapshot());
    }

    [Theory]
    [InlineData(typeof(System.Net.Sockets.SocketException), true)]
    [InlineData(typeof(InvalidDataException), true)]
    [InlineData(typeof(ZabbixProxyRefusedException), true)]
    [InlineData(typeof(InvalidOperationException), false)]
    [InlineData(typeof(NullReferenceException), false)]
    public void The_fault_contract_names_what_is_transient(Type failure, bool transient)
        => Assert.Equal(transient, ZabbixProxyPaging.IsTransient((Exception)Activator.CreateInstance(failure)!, CancellationToken.None));
}
