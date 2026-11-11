// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading.Channels;
using Community.Zabbix.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Provides the verbs of a push agent's builder, the same on the builder <see cref="ZabbixPushAgent.CreateBuilder"/> returns
/// and on the one <see cref="ZabbixPushAgentServiceCollectionExtensions.AddZabbixPushAgent"/> returns. Each verb writes the
/// agent's named options, which apply in the order of the calls, and returns the builder it was called on. The store
/// verbs, the ones a proxy has too, are <see cref="ZabbixStoreBuilderExtensions"/>'.
/// </summary>
/// <remarks>
/// The options keep the text that configuration spells, a server as <c>host:port</c>, so <see cref="Bind"/> can fill them.
/// The verbs take the .NET types and write that text. A delegate's shape says what it needs: nothing, the poll's
/// cancellation, or a service from the agent's container.
/// </remarks>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
public static class ZabbixPushAgentBuilderExtensions
{
    private static readonly TimeSpan s_meterInterval = TimeSpan.FromSeconds(30);

    extension<TBuilder>(TBuilder builder) where TBuilder : IZabbixPushAgentBuilder
    {
        /// <summary>
        /// Adds a destination, a server or proxy that receives every value, in Zabbix's spelling. Each call adds one
        /// destination, with its own store and connection. The nodes of one HA cluster are one destination, joined by
        /// <c>;</c>: <c>"zbx-1;zbx-2"</c>.
        /// </summary>
        /// <param name="server">
        /// The destination: <c>host</c> or <c>host:port</c>, on port 10051 when it names none, or the nodes of one HA cluster,
        /// tried in order. An IPv6 address with a port is bracketed: <c>[::1]:10051</c>.
        /// </param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="server"/> is empty, isn't a node, names a node twice, or names more than one destination (<c>,</c>).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
        public TBuilder AddServer(string server)
        {
            ArgumentNullException.ThrowIfNull(builder);
            string destination = ZabbixServerActiveText.Destination(server, nameof(server));
            builder.Services.Configure<ZabbixPushAgentOptions>(builder.Name, options => options.ServerActive = ZabbixServerActiveText.Add(options.ServerActive, destination));
            return builder;
        }

        /// <summary>
        /// Adds a destination that receives every value: one server or proxy, or the nodes of one HA cluster, tried in order.
        /// Each call adds one destination, with its own store and connection.
        /// </summary>
        /// <param name="nodes">The destination's nodes, in failover order.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException">No node, a node named twice, or a node that isn't a host and port.</exception>
        public TBuilder AddServer(params ReadOnlySpan<DnsEndPoint> nodes)
        {
            ArgumentNullException.ThrowIfNull(builder);
            string destination = ZabbixServerActiveText.Destination(nodes, nameof(nodes));
            builder.Services.Configure<ZabbixPushAgentOptions>(builder.Name, options => options.ServerActive = ZabbixServerActiveText.Add(options.ServerActive, destination));
            return builder;
        }

        /// <summary>Sends <paramref name="read"/>'s value of <paramref name="key"/> every <paramref name="interval"/>, for the agent's host.</summary>
        /// <typeparam name="T">The item's value type, which fixes what <paramref name="read"/> returns.</typeparam>
        /// <param name="key">The item.</param>
        /// <param name="interval">How often to read and send.</param>
        /// <param name="read">The value: <c>() =&gt; queue.Count</c>.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> isn't positive.</exception>
        public TBuilder AddPoll<T>(ZabbixItemKey<T> key, TimeSpan interval, Func<T> read)
            where T : ISpanParsable<T>
        {
            ArgumentNullException.ThrowIfNull(read);
            return builder.AddPollOf(key, interval, _ => _ => ValueTask.FromResult(read()));
        }

        /// <summary>Sends <paramref name="read"/>'s value of <paramref name="key"/> every <paramref name="interval"/>, for the agent's host: a read that waits on I/O.</summary>
        /// <typeparam name="T">The item's value type, which fixes what <paramref name="read"/> returns.</typeparam>
        /// <param name="key">The item.</param>
        /// <param name="interval">How often to read and send.</param>
        /// <param name="read">The value, read within the poll's cancellation: <c>ct =&gt; orders.CountAsync(ct)</c>.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> isn't positive.</exception>
        public TBuilder AddPoll<T>(ZabbixItemKey<T> key, TimeSpan interval, Func<CancellationToken, ValueTask<T>> read)
            where T : ISpanParsable<T>
        {
            ArgumentNullException.ThrowIfNull(read);
            return builder.AddPollOf(key, interval, _ => read);
        }

        /// <summary>
        /// Sends <paramref name="read"/>'s value of <paramref name="key"/> every <paramref name="interval"/>, for the agent's
        /// host, from a service of the agent's container. Each read has a scope of its own, so a scoped service works.
        /// </summary>
        /// <typeparam name="T">The item's value type, which fixes what <paramref name="read"/> returns.</typeparam>
        /// <typeparam name="TService">The service the value is read from.</typeparam>
        /// <param name="key">The item.</param>
        /// <param name="interval">How often to read and send.</param>
        /// <param name="read">The value: <c>(IOrderQueue queue) =&gt; queue.Count</c>.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> isn't positive.</exception>
        public TBuilder AddPoll<T, TService>(ZabbixItemKey<T> key, TimeSpan interval, Func<TService, T> read)
            where T : ISpanParsable<T>
            where TService : notnull
        {
            ArgumentNullException.ThrowIfNull(read);
            return builder.Requiring<TBuilder, TService>(key.Key.Name).AddPollOf(key, interval, provider => _ => ValueTask.FromResult(ZabbixServiceDelegates.Read(provider, read)));
        }

        /// <summary>
        /// Sends <paramref name="read"/>'s value of <paramref name="key"/> every <paramref name="interval"/>, for the agent's
        /// host, from a service of the agent's container: a read that waits on I/O. Each read has a scope of its own, so a
        /// scoped service works.
        /// </summary>
        /// <typeparam name="T">The item's value type, which fixes what <paramref name="read"/> returns.</typeparam>
        /// <typeparam name="TService">The service the value is read from.</typeparam>
        /// <param name="key">The item.</param>
        /// <param name="interval">How often to read and send.</param>
        /// <param name="read">The value, read within the poll's cancellation: <c>(OrdersDb db, CancellationToken ct) =&gt; db.CountAsync(ct)</c>.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> isn't positive.</exception>
        public TBuilder AddPoll<T, TService>(ZabbixItemKey<T> key, TimeSpan interval, Func<TService, CancellationToken, ValueTask<T>> read)
            where T : ISpanParsable<T>
            where TService : notnull
        {
            ArgumentNullException.ThrowIfNull(read);
            return builder.Requiring<TBuilder, TService>(key.Key.Name).AddPollOf(key, interval, provider => cancellationToken => ZabbixServiceDelegates.ReadAsync(provider, read, cancellationToken));
        }

        /// <summary>
        /// Sends what <paramref name="collect"/> returns every <paramref name="interval"/>: a value for any host and key, for
        /// items known only at run time.
        /// </summary>
        /// <param name="interval">How often to collect and send.</param>
        /// <param name="collect">One value: <c>() =&gt; new ZabbixValue("web-01", "app.memory", memory, DateTimeOffset.UtcNow)</c>.</param>
        /// <returns>The builder.</returns>
        public TBuilder AddPoll(TimeSpan interval, Func<ZabbixValue> collect)
        {
            ArgumentNullException.ThrowIfNull(collect);
            return builder.AddPoll(interval, _ => ValueTask.FromResult<IEnumerable<ZabbixValue>>([collect()]));
        }

        /// <summary>Sends what <paramref name="collect"/> returns every <paramref name="interval"/>: a value for any host and key, read asynchronously.</summary>
        /// <param name="interval">How often to collect and send.</param>
        /// <param name="collect">One value, collected within the poll's cancellation.</param>
        /// <returns>The builder.</returns>
        public TBuilder AddPoll(TimeSpan interval, Func<CancellationToken, ValueTask<ZabbixValue>> collect)
        {
            ArgumentNullException.ThrowIfNull(collect);
            return builder.AddPoll(interval, async cancellationToken => (IEnumerable<ZabbixValue>)[await collect(cancellationToken).ConfigureAwait(false)]);
        }

        /// <summary>Sends what <paramref name="collect"/> returns every <paramref name="interval"/>: several values for any hosts and keys.</summary>
        /// <param name="interval">How often to collect and send.</param>
        /// <param name="collect">The values, collected within the poll's cancellation.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> isn't positive.</exception>
        public TBuilder AddPoll(TimeSpan interval, Func<CancellationToken, ValueTask<IEnumerable<ZabbixValue>>> collect)
        {
            ArgumentNullException.ThrowIfNull(collect);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            return builder.AddSource(provider => ZabbixPoll.Every(interval, collect,
                provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ZabbixPoll))));
        }

        /// <summary>
        /// Adds a source of values, which <paramref name="source"/> makes from the agent's container when the agent starts. The
        /// agent reads it for as long as it runs, and a stop ends it at once.
        /// </summary>
        /// <param name="source">Makes the source: <c>services =&gt; feed.ReadAllAsync()</c>.</param>
        /// <returns>The builder.</returns>
        public TBuilder AddSource(Func<IServiceProvider, IAsyncEnumerable<ZabbixValue>> source)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(source);
            builder.Services.Configure<ZabbixPushAgentOptions>(builder.Name, options => options.Sources.Add(source));
            return builder;
        }

        /// <summary>
        /// Adds a channel's reader as a source that ends by completion: its producer completes the channel, and the agent's
        /// stop reads it to its end, within <see cref="ZabbixSendOptions.DrainTimeout"/>, rather than cutting it short. Use it
        /// for a producer that has already promised what's in the channel to someone, and stop that producer before the
        /// agent. The agent is the channel's one reader.
        /// </summary>
        /// <param name="source">Makes the reader from the agent's container: <c>_ =&gt; orders.Reader</c>.</param>
        /// <returns>The builder.</returns>
        public TBuilder AddSource(Func<IServiceProvider, ChannelReader<ZabbixValue>> source)
        {
            ArgumentNullException.ThrowIfNull(source);
            return builder.AddSource(provider => new ZabbixCompletingSource<ZabbixValue>(source(provider)));
        }

        /// <summary>
        /// Sends what a <see cref="System.Diagnostics.Metrics.Meter"/> of your application records, every 30 seconds, for the
        /// agent's host. Each instrument's name is the item key, and a measurement's tags are the key's parameters, in the
        /// order of their names. A counter sends its running total, and a gauge and an observable instrument send what they
        /// last said. A histogram is left out, because it has no one value to send.
        /// </summary>
        /// <param name="meterName">The meter's name.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="meterName"/> is empty.</exception>
        public TBuilder AddMeter(string meterName) => builder.AddMeter(meterName, s_meterInterval);

        /// <summary>Sends what a <see cref="System.Diagnostics.Metrics.Meter"/> of your application records, every <paramref name="interval"/>, for the agent's host.</summary>
        /// <param name="meterName">The meter's name.</param>
        /// <param name="interval">How often to send.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="meterName"/> is empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> isn't positive.</exception>
        public TBuilder AddMeter(string meterName, TimeSpan interval)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(meterName);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            string name = builder.Name;
            builder.Services.Configure<ZabbixPushAgentOptions>(name, options => options.ReportsForHost = true);
            return builder.AddSource(provider =>
            {
                IOptionsMonitor<ZabbixPushAgentOptions> options = provider.GetRequiredService<IOptionsMonitor<ZabbixPushAgentOptions>>();
                return new ZabbixMeterSource(meterName, interval, () => HostOf(options.Get(name), name),
                    provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILoggerFactory>().CreateLogger<ZabbixMeterSource>());
            });
        }

        /// <summary>
        /// Adds the agent's health check to the container's health checks, as <c>zabbix-agent-{name}</c> with the
        /// <c>zabbix</c> and <c>agent</c> tags. A web host maps them with <c>MapHealthChecks</c>, and a worker publishes them
        /// through an <c>IHealthCheckPublisher</c>. With several destinations, the check reports the worst.
        /// </summary>
        /// <returns>The builder.</returns>
        public TBuilder AddHealthCheck() => builder.AddHealthCheck(static _ => { });

        /// <summary>Adds the agent's health check, as <c>AddHealthCheck()</c> does, with its thresholds set.</summary>
        /// <param name="configure">Sets the thresholds.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="OptionsValidationException">The thresholds don't validate.</exception>
        public TBuilder AddHealthCheck(Action<ZabbixAgentHealthCheckOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            string name = builder.Name;
            ZabbixAgentHealthChecks.Add(builder.Services, "zabbix-agent-" + name, provider => provider.GetRequiredKeyedService<ZabbixPushAgent>(name).Statuses, configure);
            return builder;
        }

        /// <summary>Sets any of the agent's options, in order with the other verbs.</summary>
        /// <param name="configure">Sets the options.</param>
        /// <returns>The builder.</returns>
        /// <example>
        /// <code language="csharp">
        /// builder.Configure(options => options.Send.BatchSize = 100);
        /// </code>
        /// </example>
        public TBuilder Configure(Action<ZabbixPushAgentOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            builder.Services.Configure(builder.Name, configure);
            return builder;
        }

        /// <summary>
        /// Binds the agent's options to a configuration section, in order with the other verbs: <c>ServerActive</c>,
        /// <c>Hostname</c>, <c>MaxItems</c>, <c>Overflow</c>, and the <c>Connection</c> and <c>Send</c> subsections, as
        /// <see cref="ZabbixPushAgentOptions"/> names them. The section is read when the agent is built.
        /// </summary>
        /// <param name="section">The agent's section.</param>
        /// <returns>The builder.</returns>
        public TBuilder Bind(IConfiguration section)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(section);
            builder.Services.AddOptions<ZabbixPushAgentOptions>(builder.Name).Bind(section);
            return builder;
        }

        // A typed poll: the host is the agent's, read when the agent starts, and the value's type is the key's. The reader is
        // made from the agent's container when the agent starts.
        private TBuilder AddPollOf<T>(ZabbixItemKey<T> key, TimeSpan interval, Func<IServiceProvider, Func<CancellationToken, ValueTask<T>>> reader)
            where T : ISpanParsable<T>
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            string name = builder.Name;
            builder.Services.Configure<ZabbixPushAgentOptions>(name, options => options.ReportsForHost = true);
            return builder.AddSource(provider =>
            {
                string host = HostOf(provider.GetRequiredService<IOptionsMonitor<ZabbixPushAgentOptions>>().Get(name), name);
                TimeProvider time = provider.GetRequiredService<TimeProvider>();
                Func<CancellationToken, ValueTask<T>> read = reader(provider);
                return ZabbixPoll.Every(
                    interval,
                    async cancellationToken => (IEnumerable<ZabbixValue>)[ZabbixValue.From(host, key, await read(cancellationToken).ConfigureAwait(false), time.GetUtcNow())],
                    time,
                    provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ZabbixPoll)));
            });
        }
    }

    // A poll that reads a service: the agent's registration checks the container has it when it builds the agent.
    private static TBuilder Requiring<TBuilder, TService>(this TBuilder builder, string keyName)
        where TBuilder : IZabbixPushAgentBuilder
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Configure<ZabbixPushAgentOptions>(builder.Name, options => options.Requires.Add((typeof(TService), keyName)));
        return builder;
    }

    // The host typed polls and meters report for; the validator has required it at start.
    private static string HostOf(ZabbixPushAgentOptions options, string name)
        => options.Hostname ?? throw new InvalidOperationException($"Agent '{name}' has typed polls or a meter, and they need a host to report for: set Hostname.");
}
