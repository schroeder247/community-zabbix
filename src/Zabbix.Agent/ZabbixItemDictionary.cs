// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Community.Zabbix.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Community.Zabbix;

/// <summary>
/// The item keys an agent answers, by name. A key's name (<c>agent.ping</c>, <c>vfs.fs.size</c>) holds one answer, which
/// receives the whole key with its parameters; a key with no answer, or one <see cref="KeyRules"/> deny, is not supported.
/// </summary>
/// <remarks>
/// <para>
/// It behaves as any dictionary does: <see cref="Add(string, ZabbixItemCallback)"/> refuses a name already held, and the
/// indexer replaces. It is live: what you change while an agent runs is answered from the next check on. When an agent
/// starts, it adds the four keys every Zabbix agent answers and the bundled items under every name you left free, so a key
/// you added first answers instead of Zabbix's.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// agent.Items
///     .Add("app.version", _ => "2.4.1")
///     .Add(diskFree, key => new DriveInfo(key[0]).AvailableFreeSpace)
///     .Add(new QueueItems(queues));
/// </code>
/// </example>
public sealed class ZabbixItemDictionary : IDictionary<string, ZabbixItemCallback>, IReadOnlyDictionary<string, ZabbixItemCallback>, IZabbixItemRegistry
{
    private readonly ConcurrentDictionary<string, byte> _faulted = new(StringComparer.Ordinal);   // key names whose handler threw, logged once
    private readonly ConcurrentDictionary<string, ZabbixItemCallback> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ZabbixItemProvider> _owners = new(StringComparer.Ordinal);   // names a provider answers
    private readonly List<IAsyncDisposable> _registered = [];                                           // what Register built, released with the agent
    private readonly TimeProvider? _time;                                                               // the clock it was created with, for a table no agent owns

    /// <summary>An empty table, whose checks run on the system's clock unless an agent owns it.</summary>
    public ZabbixItemDictionary()
        : this(time: null)
    {
    }

    /// <summary>An empty table, whose checks' deadlines run on <paramref name="time"/> unless an agent owns it.</summary>
    /// <param name="time">The clock a check's deadline runs on; the system's when <see langword="null"/>. An agent's own table runs on the agent's.</param>
    public ZabbixItemDictionary(TimeProvider? time) => _time = time;

    /// <summary>
    /// The handlers every agent has: the four every Zabbix agent answers (<c>agent.ping</c>, <c>agent.version</c>,
    /// <c>agent.hostname</c>, <c>system.uname</c>) and the bundled items, Zabbix's own system, process, file, network
    /// and web keys (<see cref="Items.ZabbixSystemItems"/> and the other families; docs/guides/bundled-items.md).
    /// </summary>
    /// <param name="hostName">What <c>agent.hostname</c> answers.</param>
    public static ZabbixItemDictionary BuiltIn(string hostName)
    {
        var handlers = new ZabbixItemDictionary();
        handlers.AddFour(hostName);
        // Nothing owns this table's collector: it stops sampling once the table is out of reach.
        Items.ZabbixBundledItems.AddTo(handlers, hostMetadata: null, new Items.ZabbixItemCollector(Items.ZabbixHostFiles.Host));
        return handlers;
    }

    private void AddFour(string hostName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        TryAdd("agent.ping", _ => "1");
        TryAdd("agent.version", _ => ZabbixWire.AgentVersion);
        TryAdd("agent.hostname", _ => hostName);
        TryAdd("system.uname", _ => RuntimeInformation.OSDescription);
    }

    /// <summary>
    /// Which keys the table answers: Zabbix's <c>AllowKey</c> and <c>DenyKey</c> rules. A denied key is answered as one
    /// nobody answers ("This agent does not answer the key."). <see cref="ZabbixKeyRules.Default"/> (every key but <c>system.run</c>)
    /// unless set; an agent sets it from its options' <c>KeyRules</c>.
    /// </summary>
    public ZabbixKeyRules KeyRules
    {
        get => Volatile.Read(ref _keyRules);
        set => Volatile.Write(ref _keyRules, value ?? throw new ArgumentNullException(nameof(value)));
    }

    private ZabbixKeyRules _keyRules = ZabbixKeyRules.Default;

    /// <summary>The agent this table belongs to, whose name, loggers and clock what <see cref="Register(Action{IZabbixItemsBuilder})"/> builds uses.</summary>
    internal ZabbixComponent? Owner { get; set; }

    /// <summary>The clock a check's deadline runs on: the owning agent's, else the one the table was created with, else the system's.</summary>
    internal TimeProvider Time => Owner?.Time ?? _time ?? TimeProvider.System;

    /// <summary>What an agent adds when it starts: the four and, when they are on, the bundled items, under every name left free.</summary>
    /// <param name="hostName">What <c>agent.hostname</c> answers.</param>
    /// <param name="hostMetadata">What <c>agent.hostmetadata</c> answers; empty when <see langword="null"/>.</param>
    /// <param name="bundled">Whether the bundled items join the built-in four.</param>
    /// <param name="collector">The agent's collector, on the agent's clock, which the bundled items read; <see langword="null"/> when they are off.</param>
    internal void AddDefaults(string hostName, string? hostMetadata, bool bundled, Items.ZabbixItemCollector? collector)
    {
        AddFour(hostName);
        if (bundled && collector is not null) Items.ZabbixBundledItems.AddTo(this, hostMetadata, collector);
    }

    /// <summary>
    /// What a registration added to an agent: its providers under the rule <see cref="Add(ZabbixItemProvider)"/> has, then
    /// each <c>AddItem</c> set over them, the application's own answer winning whichever order they were registered in.
    /// </summary>
    /// <param name="registered">The agent's <c>AddItem</c> sets.</param>
    /// <param name="providers">The agent's item providers (<c>AddItems</c>, plugins).</param>
    /// <exception cref="InvalidOperationException">Two providers list one key name, or one lists it twice.</exception>
    internal void Compose(IEnumerable<ZabbixItemDictionary> registered, IEnumerable<ZabbixItemProvider> providers)
    {
        foreach (ZabbixItemProvider provider in providers) Add(provider);
        foreach (ZabbixItemDictionary set in registered)
            foreach ((string keyName, ZabbixItemCallback handler) in set) this[keyName] = handler;
    }

    // A request is looked up by its key's name, so a handler under anything else would never be asked.
    private static void RefuseAsName(string keyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        if (!ZabbixItemKey.TryParse(keyName, out ZabbixItemKey key) || key.Parameters.Count != 0 || key.Name != keyName)
            throw new ArgumentException($"'{keyName}' is not an item key name: a name has no parameters (app.queue.depth, not app.queue.depth[orders]).", nameof(keyName));
    }

    /// <summary>Adds a built-in answer unless <paramref name="keyName"/> is held.</summary>
    internal bool TryAdd(string keyName, ZabbixItemCallback handler) => _handlers.TryAdd(keyName, handler);

    /// <summary>Adds a built-in synchronous answer unless <paramref name="keyName"/> is held.</summary>
    internal bool TryAdd(string keyName, Func<ZabbixItemKey, string> answer)
        => TryAdd(keyName, (key, _) => ValueTask.FromResult(ZabbixAgentAnswer.Supported(key, answer(key))));

    /// <summary>The names registered.</summary>
    public ICollection<string> Keys => _handlers.Keys;

    /// <inheritdoc/>
    public ICollection<ZabbixItemCallback> Values => _handlers.Values;

    IEnumerable<string> IReadOnlyDictionary<string, ZabbixItemCallback>.Keys => _handlers.Keys;

    IEnumerable<ZabbixItemCallback> IReadOnlyDictionary<string, ZabbixItemCallback>.Values => _handlers.Values;

    bool ICollection<KeyValuePair<string, ZabbixItemCallback>>.IsReadOnly => false;

    /// <inheritdoc/>
    public int Count => _handlers.Count;

    /// <summary>
    /// The handler registered for <paramref name="keyName"/>; setting it registers or <b>replaces</b> it, the dictionary's
    /// own replace (a user's handler over a built-in one, a later registration over an earlier one).
    /// </summary>
    /// <param name="keyName">The key's name, without parameters.</param>
    /// <exception cref="KeyNotFoundException">Getting: no handler is registered under it.</exception>
    public ZabbixItemCallback this[string keyName]
    {
        get => _handlers[keyName];
        set
        {
            RefuseAsName(keyName);
            ArgumentNullException.ThrowIfNull(value);
            _handlers[keyName] = value;
        }
    }

    /// <inheritdoc/>
    public bool ContainsKey(string key) => _handlers.ContainsKey(key);

    /// <inheritdoc/>
    public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ZabbixItemCallback value) => _handlers.TryGetValue(key, out value);

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, ZabbixItemCallback>> GetEnumerator() => _handlers.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Adds the answer for <paramref name="keyName"/>, refusing a name already held as
    /// <see cref="Dictionary{TKey, TValue}.Add"/> does; the indexer replaces.
    /// </summary>
    /// <param name="keyName">The key's name, without parameters.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="ArgumentException"><paramref name="keyName"/> is already held, or is not a key's name.</exception>
    public ZabbixItemDictionary Add(string keyName, ZabbixItemCallback handler)
    {
        RefuseAsName(keyName);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryAdd(keyName, handler))
            throw new ArgumentException($"The item key '{keyName}' is already answered; set the indexer to replace it.", nameof(keyName));
        return this;
    }

    /// <summary>Adds an answer that gives the value as text; a thrown exception is the reason the key is not supported.</summary>
    /// <param name="keyName">The key's name, without parameters.</param>
    /// <param name="answer">The value for a key.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="ArgumentException"><paramref name="keyName"/> is already held, or is not a key's name.</exception>
    public ZabbixItemDictionary Add(string keyName, Func<ZabbixItemKey, string> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return Add(keyName, (key, _) => ValueTask.FromResult(ZabbixAgentAnswer.Supported(key, answer(key))));
    }

    /// <summary>Adds the answer for a typed item: a value at hand. A thrown exception is the reason the item isn't supported.</summary>
    /// <typeparam name="T">The item's value type, which fixes what <paramref name="answer"/> returns.</typeparam>
    /// <param name="item">The item. Only its name counts: the dictionary answers any parameters.</param>
    /// <param name="answer">The value: <c>() =&gt; queue.Count</c>.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="ArgumentException">The item's key is already held.</exception>
    public ZabbixItemDictionary Add<T>(ZabbixItemKey<T> item, Func<T> answer)
        where T : ISpanParsable<T>
    {
        ArgumentNullException.ThrowIfNull(answer);
        return Add(item.Key.Name, (key, _) => ValueTask.FromResult(ZabbixAgentAnswer.Supported(key, item.Format(answer()))));
    }

    /// <summary>
    /// Adds the answer for a typed item from the key the server asked for, with its parameters. A thrown exception is the
    /// reason the item isn't supported.
    /// </summary>
    /// <typeparam name="T">The item's value type, which fixes what <paramref name="answer"/> returns.</typeparam>
    /// <param name="item">The item. Only its name counts: the dictionary answers any parameters.</param>
    /// <param name="answer">The value for the key asked for: <c>key =&gt; orders.Count(key[0])</c>.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="ArgumentException">The item's key is already held.</exception>
    public ZabbixItemDictionary Add<T>(ZabbixItemKey<T> item, Func<ZabbixItemKey, T> answer)
        where T : ISpanParsable<T>
    {
        ArgumentNullException.ThrowIfNull(answer);
        return Add(item.Key.Name, (key, _) => ValueTask.FromResult(ZabbixAgentAnswer.Supported(key, item.Format(answer(key)))));
    }

    /// <summary>
    /// Adds the answer for a typed item from a read that waits on I/O, within the check's deadline. A thrown exception is the
    /// reason the item isn't supported.
    /// </summary>
    /// <typeparam name="T">The item's value type, which fixes what <paramref name="answer"/> returns.</typeparam>
    /// <param name="item">The item. Only its name counts: the dictionary answers any parameters.</param>
    /// <param name="answer">The value for the key asked for, within the deadline the token carries.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="ArgumentException">The item's key is already held.</exception>
    public ZabbixItemDictionary Add<T>(ZabbixItemKey<T> item, Func<ZabbixItemKey, CancellationToken, ValueTask<T>> answer)
        where T : ISpanParsable<T>
    {
        ArgumentNullException.ThrowIfNull(answer);
        return Add(item.Key.Name, async (key, cancellationToken) => ZabbixAgentAnswer.Supported(key, item.Format(await answer(key, cancellationToken).ConfigureAwait(false))));
    }

    /// <summary>
    /// Adds an asynchronous answer that gives the value as text; a thrown exception is the reason the key is not supported,
    /// and the token is the check's deadline.
    /// </summary>
    /// <param name="keyName">The key's name, without parameters.</param>
    /// <param name="answer">The value for a key, within the check's deadline.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="ArgumentException"><paramref name="keyName"/> is already held, or is not a key's name.</exception>
    public ZabbixItemDictionary Add(string keyName, Func<ZabbixItemKey, CancellationToken, ValueTask<string>> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return Add(keyName, async (key, cancellationToken) => ZabbixAgentAnswer.Supported(key, await answer(key, cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>
    /// Adds every key <paramref name="provider"/> lists, refusing a name already held. Nothing is added when one is refused.
    /// The provider stays yours.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="ArgumentException">A key it lists is already held, it lists one twice, or it lists none.</exception>
    public ZabbixItemDictionary Add(ZabbixItemProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        IReadOnlyList<ZabbixItemDefinition> items = provider.Items
            ?? throw new ArgumentException($"The item provider {provider.GetType().FullName} lists no items: its Items is null.", nameof(provider));
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (ZabbixItemDefinition item in items)
        {
            if (!listed.Add(item.KeyName))
                throw new ArgumentException($"The item provider {provider.GetType().FullName} lists the key '{item.KeyName}' twice.", nameof(provider));
            if (_owners.TryGetValue(item.KeyName, out ZabbixItemProvider? owner))
                throw new ArgumentException($"Two item providers list the key '{item.KeyName}': {owner.GetType().FullName} and {provider.GetType().FullName}. A key name is one provider's; set the indexer if the application answers it itself.", nameof(provider));
            if (_handlers.ContainsKey(item.KeyName))
                throw new ArgumentException($"The item key '{item.KeyName}' that {provider.GetType().FullName} lists is already answered; add the provider first, then set the indexer to replace its key.", nameof(provider));
        }
        foreach (ZabbixItemDefinition item in items)
        {
            Add(item.KeyName, provider.GetValueAsync);
            _owners[item.KeyName] = provider;
        }
        return this;
    }

    /// <summary>
    /// Runs <paramref name="configure"/> and adds the providers and keys it registers, as a registration's are composed: its
    /// providers first, then its <c>AddItem</c> keys over them. What it registers, and what those providers need, is
    /// released with the agent.
    /// </summary>
    /// <param name="configure">The registration: a library's, or a plugin's entry.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="ArgumentException">A provider lists a key the dictionary already answers.</exception>
    /// <example>
    /// <code>agent.Items.Register(items => items.AddQueueItems());</code>
    /// </example>
    public ZabbixItemDictionary Register(Action<IZabbixItemsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var services = new ServiceCollection();
        services.AddSingleton(Owner?.Loggers ?? NullLoggerFactory.Instance);
        services.AddSingleton(Time);
        services.AddLogging();
        var builder = new ZabbixRegistration(Owner?.Name ?? nameof(Items), services);
        configure(builder);
        ServiceProvider built = services.BuildServiceProvider();
        lock (_registered) _registered.Add(built);
        var key = new ZabbixItemsKey(builder.Name);
        Compose(built.GetKeyedServices<ZabbixItemDictionary>(key), built.GetKeyedServices<ZabbixItemProvider>(key));
        return this;
    }

    IZabbixItemRegistry IZabbixItemRegistry.Register(Action<IZabbixItemsBuilder> configure) => Register(configure);

    /// <summary>Releases what <see cref="Register(Action{IZabbixItemsBuilder})"/> built, when the agent is disposed.</summary>
    internal async ValueTask ReleaseAsync()
    {
        IAsyncDisposable[] registered;
        lock (_registered)
        {
            registered = [.. _registered];
            _registered.Clear();
        }
        foreach (IAsyncDisposable resource in registered) await resource.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The synchronous form, for a container disposed synchronously: what Register built is told to release, and not awaited.</summary>
    internal void ReleaseAll()
    {
        ValueTask releasing = ReleaseAsync();
        if (!releasing.IsCompletedSuccessfully) _ = releasing.AsTask();
    }

    private sealed record ZabbixRegistration(string Name, IServiceCollection Services) : IZabbixItemsBuilder;

    /// <summary>Removes a handler.</summary>
    /// <param name="keyName">The key's name.</param>
    /// <returns>Whether a handler was registered under it.</returns>
    public bool Remove(string keyName)
    {
        _owners.TryRemove(keyName, out _);
        return _handlers.TryRemove(keyName, out _);
    }

    /// <summary>Removes every handler, the built-in ones included.</summary>
    public void Clear()
    {
        _owners.Clear();
        _handlers.Clear();
    }

    void IDictionary<string, ZabbixItemCallback>.Add(string key, ZabbixItemCallback value) => Add(key, value);

    void ICollection<KeyValuePair<string, ZabbixItemCallback>>.Add(KeyValuePair<string, ZabbixItemCallback> item) => Add(item.Key, item.Value);

    bool ICollection<KeyValuePair<string, ZabbixItemCallback>>.Contains(KeyValuePair<string, ZabbixItemCallback> item)
        => ((ICollection<KeyValuePair<string, ZabbixItemCallback>>)_handlers).Contains(item);

    void ICollection<KeyValuePair<string, ZabbixItemCallback>>.CopyTo(KeyValuePair<string, ZabbixItemCallback>[] array, int arrayIndex)
        => ((ICollection<KeyValuePair<string, ZabbixItemCallback>>)_handlers).CopyTo(array, arrayIndex);

    bool ICollection<KeyValuePair<string, ZabbixItemCallback>>.Remove(KeyValuePair<string, ZabbixItemCallback> item)
        => ((ICollection<KeyValuePair<string, ZabbixItemCallback>>)_handlers).Remove(item);

    /// <summary>
    /// Answers <paramref name="key"/> as an agent would, without a server: through its handler, within
    /// <paramref name="timeout"/> on the table's clock, its agent's or the one it was created with. Use it to try a table, and
    /// the providers an agent composed into it, before a server asks; <c>Test-ZbxAgentItem</c> answers this way.
    /// </summary>
    /// <param name="key">The key, its parameters included.</param>
    /// <param name="timeout">The check's deadline, as an agent's item timeout is.</param>
    /// <param name="cancellationToken">The caller's stop.</param>
    /// <returns>
    /// The value, or why the key isn't supported: no handler is "This agent does not answer the key."; a handler that runs past
    /// <paramref name="timeout"/> is "The key did not answer before its deadline." (<c>src/go/internal/agent/scheduler/task.go</c>);
    /// a handler that throws answers with its message. The caller stopping asking isn't an answer: it propagates as
    /// <see cref="OperationCanceledException"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is neither positive nor infinite.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <example>
    /// <code language="csharp">
    /// ZabbixAgentAnswer answer = await agent.Items.AnswerAsync(ZabbixItemKey.Parse("app.version"), TimeSpan.FromSeconds(3));
    /// </code>
    /// </example>
    public ValueTask<ZabbixAgentAnswer> AnswerAsync(ZabbixItemKey key, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The deadline is positive, or infinite.");
        return AnswerLoggedAsync(key, timeout, Time, logger: null, cancellationToken);
    }

    /// <summary>As <see cref="AnswerAsync(ZabbixItemKey, TimeSpan, CancellationToken)"/>, on <paramref name="time"/>; a handler's throw is logged (3211, Warning, with the exception) the first time each key name throws.</summary>
    internal async ValueTask<ZabbixAgentAnswer> AnswerLoggedAsync(ZabbixItemKey key, TimeSpan timeout, TimeProvider time, Microsoft.Extensions.Logging.ILogger? logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (!KeyRules.Allows(key) || !_handlers.TryGetValue(key.Name, out ZabbixItemCallback? handler)) return ZabbixAgentAnswer.NotSupported(key, "This agent does not answer the key.");
        using var expiry = new CancellationTokenSource(timeout, time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        try
        {
            // A handler that ignores its token still answers by the deadline; what it is doing is left to finish on its own.
            ValueTask<ZabbixAgentAnswer> answering = handler(key, deadline.Token);
            ZabbixAgentAnswer? answer = answering.IsCompleted ? await answering.ConfigureAwait(false) : await answering.AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
            return answer ?? ZabbixAgentAnswer.NotSupported(key, "The key's handler gave no answer.");
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return ZabbixAgentAnswer.NotSupported(key, "The key did not answer before its deadline.");
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            // The server sees only the message, as a not-supported reason; the stack trace is the operator's, once per key.
            if (logger is not null && _faulted.TryAdd(key.Name, 0)) AgentLog.ItemHandlerFaulted(logger, key.Name, failure);
            return ZabbixAgentAnswer.NotSupported(key, failure.Message);
        }
    }
}
