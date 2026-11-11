// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Listening;

/// <summary>
/// Builds a <see cref="ZabbixListener"/> on Kestrel's socket transport: <see cref="ZabbixListener.CreateBuilder"/> returns one.
/// Its verbs say where it binds, who may connect, and what answers a request, and <see cref="Build"/> checks its options and
/// returns it. The verbs that say who may connect, <c>AllowPeer</c> and <c>AllowPsk</c>, are the ones a passive agent and a
/// proxy have (<see cref="ZabbixListenerBuilderExtensions"/>).
/// </summary>
/// <remarks>
/// A listener has no registration of its own: in an application with a host, a passive agent, a proxy or a push agent's
/// trapper is the listener the host runs. To bring a transport of your own, construct the listener with it.
/// </remarks>
/// <example>
/// <code language="csharp">
/// Channel&lt;ZabbixValue&gt; values = Channel.CreateBounded&lt;ZabbixValue&gt;(
///     new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.Wait });
/// await using ZabbixListener listener = ZabbixListener.CreateBuilder()
///     .UseSocketTransport(10051)
///     .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
///     .OnRequest(new ZabbixTrapper(values.Writer).Handler)
///     .Build();
/// await listener.StartingAsync();
/// await listener.StartAsync();
/// </code>
/// </example>
public sealed class ZabbixListenerBuilder : IZabbixListenerBuilder
{
    private readonly ZabbixListenerOptions _options = new();
    private Func<ZabbixConversationContext, CancellationToken, ValueTask>? _conversation;
    private ILoggerFactory? _loggers;
    private TimeProvider? _time;
    private IMeterFactory? _meters;
    private bool _built;

    /// <summary>A builder for a listener on port 10051 of every interface, until <c>UseSocketTransport</c> says otherwise.</summary>
    public ZabbixListenerBuilder()
    {
    }

    /// <summary>Binds Kestrel's socket transport on <paramref name="endPoint"/>. A later call replaces it.</summary>
    /// <param name="endPoint">The address and port; port <c>0</c> lets the platform pick one.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixListenerBuilder UseSocketTransport(IPEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        ThrowIfBuilt();
        _options.Address = endPoint.Address.ToString();
        _options.Port = endPoint.Port;
        return this;
    }

    /// <summary>Binds Kestrel's socket transport on <paramref name="port"/> of every interface. A later call replaces it.</summary>
    /// <param name="port">The port; <c>0</c> lets the platform pick one.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> isn't 0 to 65535.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixListenerBuilder UseSocketTransport(int port)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);
        return UseSocketTransport(new IPEndPoint(IPAddress.Any, port));
    }

    /// <summary>
    /// Answers each connection's request with <paramref name="handler"/>: one frame in, one frame out, as a trapper and a
    /// passive agent answer. A handler that throws is answered <c>failed</c>. A later <c>OnRequest</c> replaces it.
    /// </summary>
    /// <param name="handler">The reply's payload for a request: <c>new ZabbixTrapper(values.Writer).Handler</c>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixListenerBuilder OnRequest(Func<ZabbixRequest, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnRequest(ZabbixConversationContext.Reply(handler));
    }

    /// <summary>
    /// Hands each connection, after its first frame, to <paramref name="conversation"/>, which reads and writes the frames
    /// that follow, as a passive proxy's conversations with its server go. A later <c>OnRequest</c> replaces it.
    /// </summary>
    /// <param name="conversation">The connection: its request, and the frames after it, both ways.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="conversation"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixListenerBuilder OnRequest(Func<ZabbixConversationContext, CancellationToken, ValueTask> conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ThrowIfBuilt();
        _conversation = conversation;
        return this;
    }

    /// <summary>
    /// Accepts TLS with certificates, read from their files when the listener starts: the listener presents
    /// <paramref name="certificateFile"/>, and takes a peer whose certificate the authorities in <paramref name="caFile"/>
    /// signed, as Zabbix's <c>TLSCertFile</c>, <c>TLSKeyFile</c> and <c>TLSCAFile</c> do. A listener that accepted only
    /// unencrypted connections, as it does unless set, then accepts certificate ones only; one that accepted more kinds keeps them.
    /// </summary>
    /// <param name="certificateFile">The listener's certificate, PEM.</param>
    /// <param name="keyFile">Its private key, PEM.</param>
    /// <param name="caFile">The certificate authorities to trust, PEM: the only trust anchor.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">A file name is empty.</exception>
    /// <exception cref="ArgumentNullException">A file name is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixListenerBuilder WithCertificate(string certificateFile, string keyFile, string caFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(caFile);
        return Configure(options =>
        {
            options.Tls ??= new ZabbixTlsOptions();
            options.Tls.CertificateFile = certificateFile;
            options.Tls.KeyFile = keyFile;
            options.Tls.CaFile = caFile;
            ZabbixListenerTls.Accept(options, ZabbixTlsAcceptModes.Certificate);
        });
    }

    /// <summary>Sets any of the listener's options, in order with the other verbs.</summary>
    /// <param name="configure">Sets the options.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    /// <example>
    /// <code language="csharp">
    /// builder.Configure(options => options.MaxConcurrentConnections = 1024);
    /// </code>
    /// </example>
    public ZabbixListenerBuilder Configure(Action<ZabbixListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ThrowIfBuilt();
        configure(_options);
        return this;
    }

    /// <summary>Makes the listener log through <paramref name="loggers"/>. Without it, the listener logs nowhere. A later call replaces it.</summary>
    /// <param name="loggers">The application's logger factory. The application keeps owning it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggers"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixListenerBuilder UseLogging(ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        ThrowIfBuilt();
        _loggers = loggers;
        return this;
    }

    /// <summary>Makes the listener's deadlines run on <paramref name="time"/>, not the system's clock. A later call replaces it.</summary>
    /// <param name="time">The clock a connection's deadline runs on.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixListenerBuilder UseTimeProvider(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        ThrowIfBuilt();
        _time = time;
        return this;
    }

    /// <summary>Makes the listener count on the application's meters. Without it, the listener owns its meter. A later call replaces it.</summary>
    /// <param name="meters">The application's meter factory. The application keeps owning it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="meters"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixListenerBuilder UseMeterFactory(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        ThrowIfBuilt();
        _meters = meters;
        return this;
    }

    /// <summary>
    /// Builds the listener: checks its options with the rules it checks when it starts, and returns it, not yet bound.
    /// <see cref="Build"/> runs once, and the options can't change after it.
    /// </summary>
    /// <returns>The listener, which you must dispose. <see cref="ZabbixListener.StartingAsync"/> binds it, and <see cref="ZabbixListener.StartAsync"/> starts accepting.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Build"/> ran before, no <c>OnRequest</c> says what answers a request, or the options don't validate: the
    /// message says what to set, and the <see cref="OptionsValidationException"/> inside holds each failure.
    /// </exception>
    public ZabbixListener Build()
    {
        if (_built) throw new InvalidOperationException("This builder has built its listener. Create another builder for another listener.");
        _built = true;
        Func<ZabbixConversationContext, CancellationToken, ValueTask> conversation = _conversation
            ?? throw new InvalidOperationException("The listener can't be built as it's set: OnRequest says what answers a request.");
        ValidateOptionsResult valid = new ZabbixListenerOptionsValidator().Validate(Options.DefaultName, _options);
        if (valid.Failed)
        {
            var invalid = new OptionsValidationException(Options.DefaultName, typeof(ZabbixListenerOptions), valid.Failures);
            throw new InvalidOperationException($"The listener can't be built as it's set: {invalid.Message}", invalid);
        }
        ILoggerFactory loggers = _loggers ?? NullLoggerFactory.Instance;
        return new ZabbixListener(_options, conversation, ZabbixSocketTransport.Create(loggers), _time, loggers.CreateLogger<ZabbixListener>(), _meters);
    }

    // A listener's options are this builder's own until it builds.
    void IZabbixListenerBuilder.ConfigureListener(Action<ZabbixListenerOptions> configure) => Configure(configure);

    private void ThrowIfBuilt()
    {
        if (_built) throw new InvalidOperationException("This builder has built its listener, which can't change. Create another builder for another listener.");
    }
}
