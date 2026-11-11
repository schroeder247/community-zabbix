// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using Community.Zabbix.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix;

/// <summary>
/// Provides <c>AddItem</c>, which adds one key to an agent that answers keys, the active or the passive one, on any of their
/// builders. A delegate answers the key, its shape says what it needs, and the key's type fixes the type of the value it
/// returns.
/// </summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>Delegate</term><description>For</description></listheader>
/// <item><term><c>Func&lt;T&gt;</c></term><description>A value at hand: <c>() =&gt; queue.Count</c>.</description></item>
/// <item><term><c>Func&lt;ZabbixItemKey, T&gt;</c></term><description>A key with parameters: <c>key =&gt; orders.Count(key[0])</c>.</description></item>
/// <item><term><c>Func&lt;ZabbixItemKey, CancellationToken, ValueTask&lt;T&gt;&gt;</c></term><description>A read that waits on I/O, within the check's deadline.</description></item>
/// <item><term><c>Func&lt;TService, T&gt;</c></term><description>A service from the agent's container: <c>(IOrderQueue queue) =&gt; queue.Count</c>.</description></item>
/// <item><term><c>Func&lt;TService, CancellationToken, ValueTask&lt;T&gt;&gt;</c></term><description>A service, and a read that waits on I/O.</description></item>
/// </list>
/// <para>
/// A thrown exception is the reason the key isn't supported. A service is resolved for each check in a scope of its own, as a
/// request's are in a web host. The application's keys replace a provider's and a built-in key's, whichever order they were
/// added in. More than one service, or state kept between checks, is a <see cref="ZabbixItemProvider"/>, added with
/// <c>AddItems</c>.
/// </para>
/// </remarks>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
public static class ZabbixItemBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IZabbixItemsBuilder
    {
        /// <summary>Answers <paramref name="key"/> with a value at hand.</summary>
        /// <typeparam name="T">The item's value type, which fixes what <paramref name="answer"/> returns.</typeparam>
        /// <param name="key">The item. Only its name counts: the agent answers it with any parameters.</param>
        /// <param name="answer">The value: <c>() =&gt; queue.Count</c>.</param>
        /// <returns>The builder.</returns>
        public TBuilder AddItem<T>(ZabbixItemKey<T> key, Func<T> answer)
            where T : ISpanParsable<T>
        {
            ArgumentNullException.ThrowIfNull(answer);
            return builder.AddSet(new ZabbixItemDictionary().Add(key, answer));
        }

        /// <summary>Answers <paramref name="key"/> from the key the server asked for, with its parameters.</summary>
        /// <typeparam name="T">The item's value type, which fixes what <paramref name="answer"/> returns.</typeparam>
        /// <param name="key">The item. Only its name counts: the agent answers it with any parameters.</param>
        /// <param name="answer">The value for the key asked for: <c>key =&gt; orders.Count(key[0])</c>.</param>
        /// <returns>The builder.</returns>
        public TBuilder AddItem<T>(ZabbixItemKey<T> key, Func<ZabbixItemKey, T> answer)
            where T : ISpanParsable<T>
        {
            ArgumentNullException.ThrowIfNull(answer);
            return builder.AddSet(new ZabbixItemDictionary().Add(key, answer));
        }

        /// <summary>Answers <paramref name="key"/> with a read that waits on I/O, within the check's deadline.</summary>
        /// <typeparam name="T">The item's value type, which fixes what <paramref name="answer"/> returns.</typeparam>
        /// <param name="key">The item. Only its name counts: the agent answers it with any parameters.</param>
        /// <param name="answer">The value for the key asked for, within the deadline the token carries: <c>(key, ct) =&gt; orders.CountAsync(key[0], ct)</c>.</param>
        /// <returns>The builder.</returns>
        public TBuilder AddItem<T>(ZabbixItemKey<T> key, Func<ZabbixItemKey, CancellationToken, ValueTask<T>> answer)
            where T : ISpanParsable<T>
        {
            ArgumentNullException.ThrowIfNull(answer);
            return builder.AddSet(new ZabbixItemDictionary().Add(key, answer));
        }

        /// <summary>Answers <paramref name="key"/> from a service of the agent's container, resolved for each check.</summary>
        /// <typeparam name="T">The item's value type, which fixes what <paramref name="answer"/> returns.</typeparam>
        /// <typeparam name="TService">The service the value is read from.</typeparam>
        /// <param name="key">The item. Only its name counts: the agent answers it with any parameters.</param>
        /// <param name="answer">The value: <c>(IOrderQueue queue) =&gt; queue.Count</c>.</param>
        /// <returns>The builder.</returns>
        public TBuilder AddItem<T, TService>(ZabbixItemKey<T> key, Func<TService, T> answer)
            where T : ISpanParsable<T>
            where TService : notnull
        {
            ArgumentNullException.ThrowIfNull(answer);
            return builder.AddServiceSet(key, provider =>
            {
                ZabbixServiceDelegates.Require<TService>(provider, key.Key.Name);
                return (asked, _) => ValueTask.FromResult(ZabbixAgentAnswer.Supported(asked, key.Format(ZabbixServiceDelegates.Read(provider, answer))));
            });
        }

        /// <summary>
        /// Answers <paramref name="key"/> from a service of the agent's container, resolved for each check, with a read that
        /// waits on I/O within the check's deadline.
        /// </summary>
        /// <typeparam name="T">The item's value type, which fixes what <paramref name="answer"/> returns.</typeparam>
        /// <typeparam name="TService">The service the value is read from.</typeparam>
        /// <param name="key">The item. Only its name counts: the agent answers it with any parameters.</param>
        /// <param name="answer">The value, within the deadline the token carries: <c>(OrdersDb db, CancellationToken ct) =&gt; db.CountAsync(ct)</c>.</param>
        /// <returns>The builder.</returns>
        public TBuilder AddItem<T, TService>(ZabbixItemKey<T> key, Func<TService, CancellationToken, ValueTask<T>> answer)
            where T : ISpanParsable<T>
            where TService : notnull
        {
            ArgumentNullException.ThrowIfNull(answer);
            return builder.AddServiceSet(key, provider =>
            {
                ZabbixServiceDelegates.Require<TService>(provider, key.Key.Name);
                return async (asked, cancellationToken) =>
                    ZabbixAgentAnswer.Supported(asked, key.Format(await ZabbixServiceDelegates.ReadAsync(provider, answer, cancellationToken).ConfigureAwait(false)));
            });
        }

        /// <summary>Answers a key named only at run time with text.</summary>
        /// <param name="keyName">The key's name, without parameters.</param>
        /// <param name="answer">The value for the key asked for, as Zabbix stores it.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="keyName"/> isn't a key's name.</exception>
        public TBuilder AddItem(string keyName, Func<ZabbixItemKey, string> answer)
            => builder.AddSet(new ZabbixItemDictionary().Add(keyName, answer));

        /// <summary>Answers a key named only at run time with text, from a read that waits on I/O within the check's deadline.</summary>
        /// <param name="keyName">The key's name, without parameters.</param>
        /// <param name="answer">The value for the key asked for, as Zabbix stores it.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="keyName"/> isn't a key's name.</exception>
        public TBuilder AddItem(string keyName, Func<ZabbixItemKey, CancellationToken, ValueTask<string>> answer)
            => builder.AddSet(new ZabbixItemDictionary().Add(keyName, answer));

        /// <summary>
        /// Answers a key named only at run time with the whole answer: a value, or the reason the key isn't supported
        /// (<see cref="ZabbixAgentAnswer.NotSupported"/>).
        /// </summary>
        /// <param name="keyName">The key's name, without parameters.</param>
        /// <param name="answer">The answer for the key asked for, within the check's deadline.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="keyName"/> isn't a key's name.</exception>
        public TBuilder AddItem(string keyName, Func<ZabbixItemKey, CancellationToken, ValueTask<ZabbixAgentAnswer>> answer)
            => builder.AddSet(new ZabbixItemDictionary().Add(keyName, answer));

        // A set of keys the agent merges into its table when it's built, over its providers.
        private TBuilder AddSet(ZabbixItemDictionary set)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.AddKeyedSingleton(new ZabbixItemsKey(builder.Name), set);
            return builder;
        }

        // A key whose answer reads a service: the set is made from the agent's container when the agent is built, which
        // refuses a service the container doesn't have.
        private TBuilder AddServiceSet<T>(ZabbixItemKey<T> key, Func<IServiceProvider, ZabbixItemCallback> answer)
            where T : ISpanParsable<T>
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.AddKeyedSingleton(new ZabbixItemsKey(builder.Name), (provider, _) => new ZabbixItemDictionary().Add(key.Key.Name, answer(provider)));
            return builder;
        }
    }
}
