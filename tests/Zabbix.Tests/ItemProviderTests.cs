// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Community.Zabbix.Tests;

/// <summary>
/// Item providers (docs/adr/0008-item-providers-and-plugins.md): a library's keys answered by an agent beside its own
/// handlers, the application's handlers first, a provider's next and the built-in four last; a key name is one
/// provider's, and a provider the container built is disposed with the agent.
/// </summary>
public sealed class ItemProviderTests
{
    private static readonly Community.Zabbix.Items.ZabbixItemCollector s_collector = new(Community.Zabbix.Items.ZabbixHostFiles.Host, TimeProvider.System);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_passive_agent_answers_a_provider_s_keys_with_their_parameters()
    {
        using IHost host = await TestHost.StartAsync(services => services.AddZabbixPassiveAgent("edge")
            .Configure(Loopback)
            .AddItems<QueueItems>()                                     // the chain keeps the passive agent's builder
            .AddItem("app.version", _ => "2.4.1"), Ct);
        using var getter = new ZabbixGetter($"127.0.0.1:{Port(host)}");

        Assert.Equal("orders:7", (await getter.GetAsync("app.queue.depth[orders]", Ct)).Value);
        Assert.Equal("2.4.1", (await getter.GetAsync("app.version", Ct)).Value);
        Assert.Equal("edge-01", (await getter.GetAsync("agent.hostname", Ct)).Value);
    }

    [Fact]
    public async Task The_application_s_keys_replace_a_provider_s_and_a_provider_s_replace_the_built_in_ones()
    {
        ZabbixItemCallback added = (key, _) => ValueTask.FromResult(ZabbixAgentAnswer.Supported(key, "added"));
        var items = new ZabbixItemDictionary();

        items.Compose([new ZabbixItemDictionary().Add("app.added", added)], [new FixedItems("provided", "app.added", "app.provided", "agent.ping")]);
        items.AddDefaults("edge-01", null, bundled: true, s_collector);

        Assert.Same(added, items["app.added"]);                                          // an AddItem set
        Assert.Equal("provided", (await items["app.provided"](ZabbixItemKey.Parse("app.provided", null), Ct)).Value);
        Assert.Equal("provided", (await items["agent.ping"](ZabbixItemKey.Parse("agent.ping", null), Ct)).Value);   // over a built-in
        Assert.True(items.ContainsKey("agent.version"));                                 // the built-ins it does not list
    }

    [Fact]
    public void Two_providers_that_list_one_key_name_are_refused_naming_both()
    {
        ZabbixItemDictionary items = new ZabbixItemDictionary().Add(new FixedItems("1", "app.x")).Add(new QueueItems());

        ArgumentException failure = Assert.Throws<ArgumentException>(() => items.Add(new OtherItems("app.x")));

        Assert.Contains("'app.x'", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(FixedItems), failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(OtherItems), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_that_lists_a_key_name_twice_is_refused()
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => new ZabbixItemDictionary().Add(new FixedItems("1", "app.x", "app.x")));

        Assert.Contains("twice", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_refused_a_name_adds_none_of_its_keys()
    {
        ZabbixItemDictionary items = new ZabbixItemDictionary().Add("app.x", _ => "own");

        Assert.Throws<ArgumentException>(() => items.Add(new FixedItems("1", "app.y", "app.x")));

        Assert.False(items.ContainsKey("app.y"));
    }

    [Fact]
    public async Task Two_registered_providers_that_list_one_key_name_fail_the_agent_s_start()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => TestHost.StartAsync(services => services.AddZabbixPassiveAgent("edge")
            .Configure(Loopback)
            .AddItems(new FixedItems("1", "app.x"))
            .AddItems(_ => new OtherItems("app.x")), Ct));
    }

    [Fact]
    public async Task A_provider_the_container_builds_is_disposed_with_the_agent_and_one_the_caller_built_is_not()
    {
        using var mine = new DisposableItems();                                 // the caller's: disposed here, after the asserts
        IHost host = await TestHost.StartAsync(services => services.AddZabbixPassiveAgent("edge")
            .Configure(Loopback)
            .AddItems<BuiltItems>()
            .AddItems(mine), Ct);
        BuiltItems built = host.Services.GetKeyedServices<ZabbixItemProvider>(new ZabbixItemsKey("edge")).OfType<BuiltItems>().Single();

        await host.StopAsync(Ct);
        host.Dispose();

        Assert.True(built.Disposed);
        Assert.False(mine.Disposed);
    }

    [Fact]
    public async Task A_library_s_registration_serves_an_agent_constructed_directly_and_what_it_builds_goes_with_the_agent()
    {
        var agent = new ZabbixPassiveAgent(LoopbackOptions());
        agent.Items.Register(items => items.AddItems<BuiltItems>());
        await agent.StartAsync(Ct);
        Assert.True(agent.Items.ContainsKey("app.built"));

        await agent.DisposeAsync();

        Assert.True(BuiltItems.LastDisposed);
    }

    [Fact]
    public async Task Items_add_register_and_add_plugins_return_the_table_so_calls_chain()
    {
        await using var agent = new ZabbixPassiveAgent(LoopbackOptions());

        ZabbixItemDictionary chained = agent.Items
            .Add("app.one", _ => "1")
            .Register(items => items.AddItems(new FixedItems("2", "app.two")).AddItem("app.three", _ => "3"))
            .AddPlugins(new ZabbixPluginOptions());                                       // lists none: loads nothing
        IZabbixItemRegistry registry = agent.Items;

        Assert.Same(agent.Items, chained);
        Assert.Same(registry, registry.Register(items => items.AddItem("app.four", _ => "4")));
        Assert.Equal(["app.four", "app.one", "app.three", "app.two"], agent.Items.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("3", (await agent.Items.AnswerAsync(ZabbixItemKey.Parse("app.three", null), TimeSpan.FromSeconds(3), Ct)).Value);
    }

    [Fact]
    public void An_agent_s_providers_are_keyed_by_its_name_where_it_composes_its_table()
    {
        var services = new ServiceCollection();
        services.AddZabbixActiveAgent("edge").AddItems(new FixedItems("1", "app.x"));
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Single(provider.GetKeyedServices<ZabbixItemProvider>(new ZabbixItemsKey("edge")));
        Assert.Empty(provider.GetKeyedServices<ZabbixItemProvider>(new ZabbixItemsKey("other")));   // one name's, not another's
    }

    [Theory]
    [InlineData("app.queue.depth[orders]")]
    [InlineData(" ")]
    public void A_definition_takes_a_key_name_without_parameters(string name)
        => Assert.ThrowsAny<ArgumentException>(() => new ZabbixItemDefinition(name, "a key"));

    [Fact]
    public void A_definition_s_test_key_is_a_key_of_its_own_name()
    {
        Assert.Equal("app.a[1]", new ZabbixItemDefinition("app.a", "a key") { TestKey = "app.a[1]" }.TestKey);
        Assert.Throws<ArgumentException>(() => new ZabbixItemDefinition("app.a", "a key") { TestKey = "app.b[1]" });
    }

    private static void Loopback(ZabbixPassiveAgentOptions options)
    {
        options.Address = "127.0.0.1";
        options.Port = 0;
        options.Hostname = "edge-01";
        options.AllowedPeers.Add("127.0.0.1");
    }

    private static ZabbixPassiveAgentOptions LoopbackOptions()
    {
        var options = new ZabbixPassiveAgentOptions();
        Loopback(options);
        return options;
    }

    private static int Port(IHost host)
        => ((IPEndPoint)host.Services.GetRequiredKeyedService<ZabbixPassiveAgent>("edge").Listener!.LocalEndPoint!).Port;

    internal sealed class QueueItems : ZabbixItemProvider
    {
        public override IReadOnlyList<ZabbixItemDefinition> Items { get; } =
            [new("app.queue.depth", "Messages waiting in a queue") { TestKey = "app.queue.depth[orders]" }];

        public override ValueTask<ZabbixAgentAnswer> GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken)
            => ValueTask.FromResult(ZabbixAgentAnswer.Supported(key, key[0] + ":7"));
    }

    internal class FixedItems(string value, params string[] names) : ZabbixItemProvider
    {
        public override IReadOnlyList<ZabbixItemDefinition> Items { get; } = [.. names.Select(name => new ZabbixItemDefinition(name, "a fixed value"))];

        public override ValueTask<ZabbixAgentAnswer> GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken)
            => ValueTask.FromResult(ZabbixAgentAnswer.Supported(key, value));
    }

    private sealed class OtherItems(params string[] names) : FixedItems("other", names);

    private sealed class BuiltItems : FixedItems, IAsyncDisposable
    {
        public BuiltItems() : base("built", "app.built") { }

        public static bool LastDisposed { get; private set; }

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            LastDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DisposableItems() : FixedItems("mine", "app.mine"), IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
