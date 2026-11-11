// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Represents a client of one Zabbix server, reached by one endpoint with one credential: the root of the SDK. It follows
/// the Azure client shape: immutable, thread-safe, a singleton per name, and mockable (a protected constructor and virtual
/// members). Its constructors take an address and a credential; <see cref="CreateBuilder"/> builds a client with more to set,
/// and <see cref="ZabbixServiceCollectionExtensions.AddZabbix(IServiceCollection, string)"/> registers one in an application's
/// container, with the same verbs. All three build it through the same registration, so it has the same HTTP pipeline and
/// resilience whichever way it's made.
/// </summary>
/// <example>
/// <code language="csharp">
/// await using var zabbix = new ZabbixClient("https://zabbix.example/", token);
///
/// var hosts = await zabbix.Hosts.GetAsync(q =>
/// {
///     q.Filter.Status = [ZabbixHostStatus.Enabled];
///     q.Search.VisibleName = "web";
/// });
///
/// ulong groupId = await zabbix.HostGroups.CreateAsync("Linux servers");
/// await zabbix.Hosts.CreateAsync("web-02", [groupId]);
/// double load = await zabbix.Hosts["web-01"].GetLastAsync(ZabbixItemKeys.Cpu.Load());
/// </code>
/// </example>
public partial class ZabbixClient : IZabbixNode, IDisposable, IAsyncDisposable
{
    private static readonly JsonTypeInfo<JsonElement> s_element = (JsonTypeInfo<JsonElement>)ZabbixJson.Options.GetTypeInfo(typeof(JsonElement));
    // Null only in a server built for mocking, whose members are overridden; Built says so to a member that is not.
    private readonly ZabbixHostScope? _all;
    private readonly Uri? _endpoint;
    private readonly Uri? _frontendUri;
    private readonly IZabbixTransport? _transport;
    private readonly ZabbixApi? _api;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The container that built the telemetry owns and disposes it.")]
    private readonly Diagnostics.ZabbixTreeTelemetry? _treeTelemetry;
    private readonly CancellationTokenSource _disposed = new();
    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private readonly ZabbixCredential _credential;
    // The container a constructor or a builder built the client in, which the client disposes; null for a client the
    // application's container holds.
    private ServiceProvider? _container;
    private VersionRead? _apiVersion;
    private int _disposedFlag;

    private static ZabbixSessionCredential FromLogin(NetworkCredential login)
    {
        ArgumentNullException.ThrowIfNull(login);
        return ZabbixCredential.FromLogin(login.UserName, login.Password);
    }

    /// <summary>Initializes a client for mocking; every member of a client built this way is to be overridden.</summary>
    protected ZabbixClient()
    {
        _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _credential = ZabbixCredential.Anonymous;
        Name = Options.DefaultName;
        Time = TimeProvider.System;
        Loggers = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        NodeLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        Stopping = _disposed.Token;
    }

    /// <summary>Initializes a client at <paramref name="endpoint"/> that sends an API token (<em>Users, API tokens</em> in the frontend).</summary>
    /// <param name="endpoint">The frontend's address, e.g. <c>https://zabbix.example/</c>, or the JSON-RPC endpoint; an absolute URL.</param>
    /// <param name="token">The API token to send with every call.</param>
    /// <exception cref="ArgumentException"><paramref name="token"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> or <paramref name="token"/> is <see langword="null"/>.</exception>
    /// <exception cref="UriFormatException"><paramref name="endpoint"/> isn't an absolute URL.</exception>
    /// <exception cref="OptionsValidationException">The endpoint is plain <c>http</c> to another host (<see cref="ZabbixOptions.AllowUnencryptedCredentials"/>).</exception>
    public ZabbixClient(string endpoint, [ZabbixSecret] string token)
        : this(AbsoluteUri(endpoint), ZabbixCredential.FromApiToken(token))
    {
    }

    /// <summary>Initializes a client at <paramref name="endpoint"/> that sends an API token (<em>Users, API tokens</em> in the frontend).</summary>
    /// <param name="endpoint">The frontend's address, e.g. <c>https://zabbix.example/</c>, or the JSON-RPC endpoint.</param>
    /// <param name="token">The API token to send with every call.</param>
    /// <exception cref="ArgumentException"><paramref name="token"/> is empty, or <paramref name="endpoint"/> isn't an absolute address.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> or <paramref name="token"/> is <see langword="null"/>.</exception>
    /// <exception cref="OptionsValidationException">The endpoint is plain <c>http</c> to another host (<see cref="ZabbixOptions.AllowUnencryptedCredentials"/>).</exception>
    public ZabbixClient(Uri endpoint, [ZabbixSecret] string token)
        : this(endpoint, ZabbixCredential.FromApiToken(token))
    {
    }

    /// <summary>Initializes a client at <paramref name="endpoint"/> that logs in with a user's name and password on its first call.</summary>
    /// <param name="endpoint">The frontend's address, e.g. <c>https://zabbix.example/</c>, or the JSON-RPC endpoint; an absolute URL.</param>
    /// <param name="login">The user's name and password; only those two are read.</param>
    /// <exception cref="ArgumentException">The user's name or password is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> or <paramref name="login"/> is <see langword="null"/>.</exception>
    /// <exception cref="UriFormatException"><paramref name="endpoint"/> isn't an absolute URL.</exception>
    /// <exception cref="OptionsValidationException">The endpoint is plain <c>http</c> to another host (<see cref="ZabbixOptions.AllowUnencryptedCredentials"/>).</exception>
    public ZabbixClient(string endpoint, NetworkCredential login)
        : this(AbsoluteUri(endpoint), FromLogin(login))
    {
    }

    /// <summary>Initializes a client at <paramref name="endpoint"/> that logs in with a user's name and password on its first call.</summary>
    /// <param name="endpoint">The frontend's address, e.g. <c>https://zabbix.example/</c>, or the JSON-RPC endpoint.</param>
    /// <param name="login">The user's name and password; only those two are read.</param>
    /// <exception cref="ArgumentException">The user's name or password is empty, or <paramref name="endpoint"/> isn't an absolute address.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> or <paramref name="login"/> is <see langword="null"/>.</exception>
    /// <exception cref="OptionsValidationException">The endpoint is plain <c>http</c> to another host (<see cref="ZabbixOptions.AllowUnencryptedCredentials"/>).</exception>
    public ZabbixClient(Uri endpoint, NetworkCredential login)
        : this(endpoint, FromLogin(login))
    {
    }

    /// <summary>Initializes a client at <paramref name="endpoint"/>, spoken to with <paramref name="credential"/>, such as one that reads a vault.</summary>
    /// <param name="endpoint">The frontend's address, e.g. <c>https://zabbix.example/</c>, or the JSON-RPC endpoint; an absolute URL.</param>
    /// <param name="credential">How requests prove who they are: your own, <see cref="ZabbixCredential.FromApiToken"/>, <see cref="ZabbixCredential.FromLogin"/>, or <see cref="ZabbixCredential.Anonymous"/> for the methods that need no authentication.</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> or <paramref name="credential"/> is <see langword="null"/>.</exception>
    /// <exception cref="UriFormatException"><paramref name="endpoint"/> isn't an absolute URL.</exception>
    /// <exception cref="OptionsValidationException">The endpoint is plain <c>http</c> to another host and the credential isn't anonymous (<see cref="ZabbixOptions.AllowUnencryptedCredentials"/>).</exception>
    public ZabbixClient(string endpoint, ZabbixCredential credential)
        : this(AbsoluteUri(endpoint), credential)
    {
    }

    /// <summary>Initializes a client at <paramref name="endpoint"/>, spoken to with <paramref name="credential"/>, such as one that reads a vault.</summary>
    /// <param name="endpoint">The frontend's address, e.g. <c>https://zabbix.example/</c>, or the JSON-RPC endpoint, <c>https://zabbix.example/api_jsonrpc.php</c> (<see cref="ZabbixOptions.GetApiEndpoint"/>).</param>
    /// <param name="credential">How requests prove who they are: your own, <see cref="ZabbixCredential.FromApiToken"/>, <see cref="ZabbixCredential.FromLogin"/>, or <see cref="ZabbixCredential.Anonymous"/> for the methods that need no authentication.</param>
    /// <remarks>
    /// It's the client <c>ZabbixClient.CreateBuilder().WithEndpoint(endpoint).WithCredential(credential).Build()</c> builds, in a
    /// container of its own that it disposes.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="endpoint"/> isn't an absolute address.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> or <paramref name="credential"/> is <see langword="null"/>.</exception>
    /// <exception cref="OptionsValidationException">The endpoint is plain <c>http</c> to another host and the credential isn't anonymous (<see cref="ZabbixOptions.AllowUnencryptedCredentials"/>).</exception>
    public ZabbixClient(Uri endpoint, ZabbixCredential credential)
        : this(ZabbixClientBuilder.Compose(endpoint, credential))
    {
    }

    // The container a constructor composed, which the client owns from here.
    private ZabbixClient(ServiceProvider container)
        : this(Options.DefaultName, container, container)
    {
    }

    /// <summary>The client the registration builds from its container, which owns it.</summary>
    /// <param name="name">The client's name, the key it's registered under.</param>
    /// <param name="services">The container the client was registered in.</param>
    internal ZabbixClient(string name, IServiceProvider services)
        : this(name, services, container: null)
    {
    }

    private ZabbixClient(string name, IServiceProvider services, ServiceProvider? container)
    {
        Name = name;
        try
        {
            ZabbixOptions options = services.GetRequiredService<IOptionsMonitor<ZabbixOptions>>().Get(name);
            _credential = services.GetRequiredKeyedService<ZabbixCredential>(name);
            _transport = services.GetRequiredKeyedService<IZabbixTransport>(name);
            _endpoint = ZabbixHttp.WithoutUserInfo(ZabbixOptions.GetApiEndpoint(options.Endpoint ?? throw new OptionsValidationException(name, typeof(ZabbixOptions), ["Endpoint is required"])));
            _frontendUri = ZabbixFrontend.BaseOf(_endpoint, options.FrontendUri);
            Time = services.GetRequiredService<TimeProvider>();
            Loggers = services.GetRequiredService<ILoggerFactory>();
            _treeTelemetry = services.GetRequiredService<Diagnostics.ZabbixTreeTelemetry>();
        }
        catch
        {
            container?.Dispose();
            _disposed.Dispose();
            throw;
        }
        _container = container;
        Stopping = _disposed.Token;
        _logger = Loggers.CreateLogger<ZabbixClient>();
        NodeLogger = Loggers.CreateLogger<ZabbixNode>();
        _api = new ZabbixApi(_transport, GetApiVersionAsync);
        _api.Hosts.Owner = this;
        _api.HostGroups.Owner = this;
        _all = new ZabbixHostScope(this, new ZabbixSelection());
    }

    /// <summary>
    /// Starts a builder for a client with more than an address and a credential to set: options, resilience, logging or the
    /// clock.
    /// </summary>
    /// <returns>A builder whose <see cref="ZabbixClientBuilder.Build"/> returns the client.</returns>
    /// <example>
    /// <code language="csharp">
    /// await using ZabbixClient zabbix = ZabbixClient.CreateBuilder()
    ///     .WithEndpoint("https://zabbix.example/")
    ///     .WithToken(token)
    ///     .WithTimeout("configuration.import", TimeSpan.FromMinutes(5))
    ///     .Build();
    /// </code>
    /// </example>
    public static ZabbixClientBuilder CreateBuilder() => new();

    /// <summary>Gives the client the container a <see cref="ZabbixClientBuilder"/> built it in, to dispose with itself.</summary>
    /// <param name="container">The builder's container, which built this client.</param>
    internal void OwnContainer(ServiceProvider container) => Volatile.Write(ref _container, container);

    /// <summary>The pull plane's telemetry: history pages, watcher ticks.</summary>
    internal Diagnostics.ZabbixTreeTelemetry TreeTelemetry => Built(_treeTelemetry);

    /// <summary>Cancelled when the client is disposed: its watchers stop with it.</summary>
    internal CancellationToken Stopping { get; }

    /// <summary>The clock every watcher on this client polls by.</summary>
    internal TimeProvider Time { get; }

    /// <summary>Where this client's nodes log.</summary>
    internal ILoggerFactory Loggers { get; }

    /// <summary>The one logger every node of this client writes to, made once.</summary>
    internal ILogger NodeLogger { get; }

    /// <summary>Every host on the server: enumerate it for the hosts, reach one by name, <c>AllHosts["web-01"]</c>, or ask it and every item answers.</summary>
    public virtual ZabbixHostScope AllHosts
    {
        get
        {
            ThrowIfDisposed();
            return Built(_all);
        }
    }

    /// <inheritdoc/>
    public virtual IAsyncEnumerable<ZabbixValue> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Built(_all).GetLatestAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public virtual IAsyncEnumerable<ZabbixValue> GetHistoryAsync(DateTimeOffset from, DateTimeOffset? till = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Built(_all).GetHistoryAsync(from, till, cancellationToken);
    }

    /// <inheritdoc/>
    public virtual IZabbixWatcher Watch(ZabbixWatchPlan plan)
    {
        ThrowIfDisposed();
        return Built(_all).Watch(plan);
    }

    /// <summary>The name it was registered under; empty for the unnamed client.</summary>
    public virtual string Name { get; }

    /// <summary>Where the server is, without any user info the configured URI carried (never sent, so never kept).</summary>
    public virtual Uri Endpoint => Built(_endpoint);

    /// <summary>
    /// Where the web frontend is: <see cref="ZabbixOptions.FrontendUri"/> when set, else the folder of
    /// <see cref="Endpoint"/> (<c>https://zabbix.example/api_jsonrpc.php</c> gives <c>https://zabbix.example/</c>).
    /// </summary>
    public virtual Uri FrontendUri => Built(_frontendUri);

    /// <summary>
    /// The web frontend of this server: <see cref="FrontendUri"/> with the server's version
    /// (<see cref="GetApiVersionAsync"/>, read once and kept), whose <see cref="ZabbixFrontend.UriFor"/> gives the page
    /// of any object: <c>(await client.GetFrontendAsync()).UriFor(host)</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the version read, when it is not yet known.</param>
    /// <returns>The frontend.</returns>
    /// <exception cref="ZabbixVersionException">The server runs a version older than 7.0.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public virtual async Task<ZabbixFrontend> GetFrontendAsync(CancellationToken cancellationToken = default)
        => new(FrontendUri, await GetApiVersionAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>The request/reply transport the clients and <see cref="SendAsync(string, JsonElement, CancellationToken)"/> share.</summary>
    internal IZabbixTransport Transport => Built(_transport);

    /// <summary>The clients the generated properties (<see cref="Hosts"/>, <see cref="Items"/>, …) hand out.</summary>
    internal ZabbixApi Api => Built(_api);

    /// <inheritdoc cref="IZabbixTransport.SendAsync{TParameters, TResult}"/>
    public virtual Task<TResult> SendAsync<TParameters, TResult>(
        string method, TParameters parameters, JsonTypeInfo<TParameters> parametersInfo, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Transport.SendAsync(method, parameters, parametersInfo, resultInfo, cancellationToken);
    }

    /// <summary>
    /// Calls <paramref name="method"/> with typed parameters and returns its raw <c>result</c>, as
    /// <see cref="SendAsync{TParameters}(string, TParameters, JsonTypeInfo{TParameters}, CancellationToken)"/> does.
    /// </summary>
    /// <typeparam name="TParameters">The type of the method's parameters.</typeparam>
    /// <param name="method">The API method, e.g. <c>host.get</c>.</param>
    /// <param name="parameters">The method's parameters.</param>
    /// <param name="parametersInfo">How to write the parameters as JSON.</param>
    /// <returns>The call's <c>result</c>, as the server answered it.</returns>
    /// <exception cref="ZabbixApiException">The API answered with an error object.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public Task<JsonElement> SendAsync<TParameters>(string method, TParameters parameters, JsonTypeInfo<TParameters> parametersInfo)
        => SendAsync(method, parameters, parametersInfo, CancellationToken.None);

    /// <inheritdoc cref="IZabbixTransport.SendAsync{TParameters}"/>
    public virtual Task<JsonElement> SendAsync<TParameters>(
        string method, TParameters parameters, JsonTypeInfo<TParameters> parametersInfo, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return Transport.SendAsync(method, parameters, parametersInfo, cancellationToken);
    }

    /// <summary>
    /// Calls <paramref name="method"/> with <paramref name="parameters"/> as its <c>params</c> and returns its raw <c>result</c>,
    /// as <see cref="SendAsync(string, JsonElement, CancellationToken)"/> does.
    /// </summary>
    /// <param name="method">The API method, e.g. <c>host.get</c>.</param>
    /// <param name="parameters">The method's parameters, as JSON.</param>
    /// <returns>The call's <c>result</c>, as the server answered it.</returns>
    /// <exception cref="ArgumentException"><paramref name="method"/> is empty.</exception>
    /// <exception cref="ZabbixApiException">The API answered with an error object.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public Task<JsonElement> SendAsync(string method, JsonElement parameters)
        => SendAsync(method, parameters, CancellationToken.None);

    /// <summary>
    /// Calls <paramref name="method"/> with <paramref name="parameters"/> as its <c>params</c> and returns its raw
    /// <c>result</c>: the raw door for a method the clients do not name, without a type of its own. Parse the parameters
    /// from text (<c>JsonDocument.Parse(text).RootElement</c>) or build them with
    /// <see cref="JsonSerializer.SerializeToElement{TValue}(TValue, JsonTypeInfo{TValue})"/>.
    /// </summary>
    /// <param name="method">The API method, e.g. <c>host.get</c>.</param>
    /// <param name="parameters">The method's parameters, as JSON.</param>
    /// <param name="cancellationToken">A cancellation token, which reaches the socket.</param>
    /// <returns>The call's <c>result</c>, as the server answered it.</returns>
    /// <exception cref="ArgumentException"><paramref name="method"/> is empty.</exception>
    /// <exception cref="ZabbixApiException">The API answered with an error object.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public virtual Task<JsonElement> SendAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ThrowIfDisposed();
        return Transport.SendAsync(method, parameters, s_element, cancellationToken);
    }

    /// <summary>
    /// Ends the login session this client's <see cref="ZabbixSessionCredential"/> holds (<c>user.logout</c>), so the next
    /// request logs in again. Nothing is sent when the credential holds no session (an API token, anonymous, or no login
    /// yet). <see cref="DisposeAsync"/> calls it too, as a best effort; call it to see the request and its failure.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token, which reaches the socket.</param>
    /// <returns>A task that completes when the session was ended.</returns>
    /// <exception cref="ZabbixApiException">The server refused the logout.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public virtual Task LogOutAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _credential is ZabbixSessionCredential { Session: { } session } login ? EndSessionAsync(login, session, cancellationToken) : Task.CompletedTask;
    }

    private async Task EndSessionAsync(ZabbixSessionCredential login, [ZabbixSecret] string session, CancellationToken cancellationToken)
    {
        await Users.LogoutAsync(cancellationToken).ConfigureAwait(false);
        login.Forget(session);
        ApiLog.LoggedOut(_logger, Name);
    }

    /// <summary>
    /// The server's API version (<c>apiinfo.version</c>, sent without an <c>Authorization</c> header), read once and kept:
    /// the frontend's version, always a plain <c>X.Y.Z</c> (<c>8.0.0</c> on 8.0.0rc1). A failed read is not kept, so the
    /// next call asks again. The generated clients gate the methods and members not in every version on it (a call to one
    /// the version does not have throws <see cref="ZabbixVersionException"/> before anything is sent; a method the server
    /// no longer has, <c>host.massupdate</c> on 8.0, is emulated with the methods it has), and the tree asks 8.0 for uncut
    /// binary and JSON history.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels this caller's wait. The read is shared by every caller waiting on it: it is cancelled at the server (the
    /// request aborted) when the last of them stops waiting, or when this client is disposed, and a cancelled read is not
    /// kept.
    /// </param>
    /// <returns>The version.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public virtual Task<Version> GetApiVersionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return WaitForApiVersionAsync(cancellationToken);
    }

    private async Task<Version> WaitForApiVersionAsync(CancellationToken cancellationToken)
    {
        VersionRead read;
        lock (_gate)
        {
            if (_apiVersion is null || _apiVersion.Task.IsFaulted || _apiVersion.Task.IsCanceled) _apiVersion = new VersionRead(ReadApiVersionAsync, Stopping);
            read = _apiVersion;
            read.Waiters++;
        }
        try
        {
            return await read.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            bool abandon;
            lock (_gate)
            {
                // The last waiter leaving an unfinished read aborts it; otherwise a cancelled wait (Ctrl+C on
                // Connect-ZbxServer) would leave the request running until the pipeline's deadline. An abandoned read is forgotten here, so
                // no later caller joins it; a finished one is released by whoever leaves it last.
                abandon = --read.Waiters == 0 && !read.Task.IsCompleted;
                if (abandon && ReferenceEquals(_apiVersion, read)) _apiVersion = null;
            }
            if (abandon) await read.AbandonAsync().ConfigureAwait(false);
            else if (read.Task.IsCompleted) read.Dispose();
        }
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposedFlag) != 0, this);

    private async Task<Version> ReadApiVersionAsync(CancellationToken cancellationToken)
    {
        string text = await ApiInfo.GetVersionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        Version version = Version.TryParse(text, out Version? parsed)
            ? parsed
            : throw new InvalidDataException($"The apiinfo.version reply '{text}' is not a version.");
        ApiLog.ApiVersion(_logger, Name, Endpoint, version);
        return version;
    }

    /// <summary>
    /// One shared <c>apiinfo.version</c> read and its abort: cancelled by the client's disposal (linked) or by its last
    /// waiter; <see cref="Waiters"/> is guarded by the client's gate. Nobody cancels a read once it finished or was
    /// abandoned, so disposing its source then (idempotent) never races a cancellation.
    /// </summary>
    private sealed class VersionRead : IDisposable
    {
        private readonly CancellationTokenSource _abort;

        public VersionRead(Func<CancellationToken, Task<Version>> read, CancellationToken stopping)
        {
            _abort = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            Task = read(_abort.Token);
        }

        public Task<Version> Task { get; }

        public int Waiters { get; set; }

        // Returns once the aborted request has ended, so no resilience execution is still pending when the caller goes
        // on (the standard handler's registry, disposed with a request in flight, polls for it every second
        // of the container's clock for up to 30 s, Polly's ExecutionTrackingComponent; on a clock that does not move,
        // for ever). The abort is prompt; the wait is for the request's unwinding, not for the server.
        public async Task AbandonAsync()
        {
            // A disposed client already cancelled it through the link (and released it).
            if (!_abort.IsCancellationRequested) await _abort.CancelAsync().ConfigureAwait(false);
            await ((Task)Task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _abort.Dispose();
        }

        public void Dispose() => _abort.Dispose();
    }

    /// <summary>
    /// Stops this client's watchers (their readers end) and, for a client its constructor or a <see cref="ZabbixClientBuilder"/>
    /// built, releases the container it was built in. A client from an application's container is disposed by that container.
    /// Safe to call more than once, from any thread. A login session (<see cref="ZabbixSessionCredential"/>) is not ended,
    /// because ending it is a request: the server expires it after its idle time. <see cref="DisposeAsync"/> and
    /// <see cref="LogOutAsync"/> end it.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases what the client holds; see <see cref="Dispose()"/>.</summary>
    /// <param name="disposing">Whether this is <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;
        _disposed.Cancel();
        _disposed.Dispose();
        lock (_gate) _apiVersion?.Dispose();                                         // cancelled through the link above
        // The container a builder built the client in disposes the client too; that call returns at once.
        Interlocked.Exchange(ref _container, null)?.Dispose();
    }

    /// <summary>
    /// Ends a login session the client's <see cref="ZabbixSessionCredential"/> holds (<c>user.logout</c>, best effort: a
    /// failure is logged at Debug and the session is left to expire), then stops this client's watchers and, for a client its
    /// constructor or a <see cref="ZabbixClientBuilder"/> built, releases the container it was built in. A client from an
    /// application's container is disposed by that container. Safe to call more than once, from any thread; it never throws.
    /// </summary>
    /// <returns>When released.</returns>
    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Ends the login session and releases what the client holds; see <see cref="DisposeAsync"/>.</summary>
    /// <returns>When released.</returns>
    protected virtual ValueTask DisposeAsyncCore() => ReleaseAsync();

    private async ValueTask ReleaseAsync()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;
        if (_credential is ZabbixSessionCredential { Session: { } session } login) await LogOutQuietlyAsync(login, session).ConfigureAwait(false);
        await _disposed.CancelAsync().ConfigureAwait(false);
        _disposed.Dispose();
        lock (_gate) _apiVersion?.Dispose();                                         // cancelled through the link above
        if (Interlocked.Exchange(ref _container, null) is { } container) await container.DisposeAsync().ConfigureAwait(false);
    }

    // Best effort by contract: a disposal that throws would leave the container unreleased, and a session that could not
    // be ended expires on the server's side. The request is bounded by the transport's own deadlines.
    private async Task LogOutQuietlyAsync(ZabbixSessionCredential login, [ZabbixSecret] string session)
    {
        try
        {
            await EndSessionAsync(login, session, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)    // whatever the request did, the disposal goes on
        {
            login.Forget(session);
            string errorType = Diagnostics.ZabbixErrorType.Of(failure);
            ApiLog.LogoutFailed(_logger, Name, errorType, failure);
        }
    }

    private static T Built<T>(T? value) where T : class
        => value ?? throw new InvalidOperationException("The server was built for mocking, and this member is not overridden.");

    // The string overloads' URL: absolute, as the frontend's address or the endpoint must be.
    private static Uri AbsoluteUri(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new Uri(endpoint, UriKind.Absolute);
    }
}
