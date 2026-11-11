// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using Community.Zabbix.PowerShell.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Represents one server the session is connected to: the SDK's <see cref="ZabbixClient"/> and the container that composed
/// it through <c>AddZabbixClient</c> — logging to the host's streams, the shell's telemetry, and OTLP export when the module
/// was imported with <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> set. A command holds a lease on it for the length of its call,
/// so closing it (a reconnect, <c>Disconnect-ZbxServer</c>, a closing runspace) never disposes the container under a
/// call still in flight in this or another runspace: the last lease out disposes it.
/// </summary>
public sealed class ZabbixConnection : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly ILogger _log;
    // 1 for the owner (released by Dispose) plus one per in-flight call. SafeHandle.DangerousAddRef/DangerousRelease
    // has exactly these semantics, but its contract is an OS handle (an IntPtr, a critical finalizer, ReleaseHandle in
    // a constrained region); a container disposed on the finalizer thread would be wrong. So the count is a plain Interlocked counter.
    private int _references = 1;
    private int _closed;
    // Guards every call into ZabbixModuleTelemetry's OpenTelemetry code, so the SDK's assemblies load only when export
    // was asked for (the JIT loads an assembly when it compiles the first method that uses it).
    private readonly bool _exports;

    private ZabbixConnection(string name, Uri endpoint, ZabbixCredential credential, ServiceProvider services, bool exports)
    {
        _exports = exports;
        Name = name;
        Endpoint = endpoint;
        Credential = credential;
        _services = services;
        Client = services.GetRequiredService<IZabbixClientFactory>().Create(name);
        Telemetry = services.GetRequiredService<ZabbixPowerShellTelemetry>();
        Logs = services.GetRequiredService<ZabbixPowerShellLoggerProvider>();
        _log = services.GetRequiredService<ILoggerFactory>().CreateLogger<ZabbixConnection>();
    }

    /// <summary>The name the session knows this server by.</summary>
    public string Name { get; }

    /// <summary>The JSON-RPC endpoint.</summary>
    public Uri Endpoint { get; }

    /// <summary>
    /// The server's trapper (its <c>ListenIP</c>:<c>ListenPort</c> as this machine reaches it), for the commands that talk
    /// the server's own protocol (<c>Get-ZbxQueue</c>, <c>Test-ZbxItem</c>, <c>Send-ZbxData</c>…); given at
    /// <c>Connect-ZbxServer -Trapper</c>, one node or the nodes of an HA cluster, tried in order. When it is not given
    /// those commands do what the frontend does (<c>ZBase.php</c>: <c>$ZBX_SERVER</c>, else the active HA node's
    /// address and port) — never the API's host with a guessed port.
    /// </summary>
    public IReadOnlyList<DnsEndPoint>? Trapper { get; internal set; }

    /// <summary>
    /// The TLS words of <see cref="Trapper"/> (<c>Connect-ZbxServer -TrapperTls</c>, <c>-TrapperPsk</c>, <c>-TrapperCertificate</c>, …): every
    /// command that speaks the server's own protocol uses them unless it is given its own; <see langword="null"/> is
    /// unencrypted.
    /// </summary>
    internal ZabbixTlsOptions? TrapperTls { get; set; }

    /// <summary>The API version the server reported when the connection was opened.</summary>
    public Version? ApiVersion { get; internal set; }

    /// <summary>The SDK root: the API clients, the tree, the transport.</summary>
    public ZabbixClient Client { get; }

    /// <summary>Whether the connection has been closed (in-flight calls may still be finishing).</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Whether the container has been disposed: closed, and the last in-flight call has returned.</summary>
    internal bool IsReleased => Volatile.Read(ref _references) == 0;

    /// <summary>How requests prove who they are; the server's own protocol takes the same token as <c>sid</c> (<c>trapper_auth.c</c>).</summary>
    internal ZabbixCredential Credential { get; }

    /// <summary>
    /// The token this connection's requests carry — the API token, or the session id a login obtained — for the server's
    /// own protocol, which accepts either as <c>sid</c> (<c>trapper_auth.c</c>: 32 characters a session, 64 a token).
    /// </summary>
    /// <param name="cancellationToken">Cancels a login.</param>
    internal ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken)
        => Credential.GetTokenAsync(new ZabbixTokenRequestContext(_services.GetRequiredKeyedService<IZabbixTransport>(Name), "server.wire", Time), cancellationToken);

    /// <summary>The clock this connection runs on, the one its client was registered with.</summary>
    internal TimeProvider Time => _services.GetRequiredService<TimeProvider>();

    /// <summary>This connection's logger factory, the one its client logs through (to the host's streams).</summary>
    internal ILoggerFactory Loggers => _services.GetRequiredService<ILoggerFactory>();

    /// <summary>The shell layer's telemetry on this connection's meter factory.</summary>
    internal ZabbixPowerShellTelemetry Telemetry { get; }

    /// <summary>The bridge from this connection's loggers to the host's streams.</summary>
    internal ZabbixPowerShellLoggerProvider Logs { get; }

    /// <summary>The connection's container.</summary>
    internal IServiceProvider Services => _services;

    /// <summary>A logger in this connection's factory.</summary>
    internal ILogger Log => _log;

    /// <summary>
    /// Composes a connection: <c>AddZabbixClient(name)</c> with its endpoint and <paramref name="credential"/>, the host's streams
    /// as the only log provider, the shell's telemetry, and OTLP export when <paramref name="exportTelemetry"/>.
    /// </summary>
    /// <param name="name">The session's name for the server.</param>
    /// <param name="endpoint">The JSON-RPC endpoint.</param>
    /// <param name="credential">How requests prove who they are.</param>
    /// <param name="exportTelemetry">Whether the module was imported with an OTLP endpoint.</param>
    /// <param name="time">The clock the connection runs on (the runspace context's).</param>
    /// <param name="frontend">The web frontend, when it is not at the endpoint's folder (<see cref="ZabbixClientOptions.FrontendUri"/>).</param>
    /// <param name="http">The HTTP knobs that differ from the SDK's pipeline (<see cref="ZabbixHttpSettings"/>).</param>
    /// <param name="allowUnencryptedCredentials">Whether a credential may go to a plain <c>http</c> endpoint on another host (<see cref="ZabbixClientOptions.AllowUnencryptedCredentials"/>).</param>
    internal static ZabbixConnection Open(string name, Uri endpoint, ZabbixCredential credential, bool exportTelemetry, TimeProvider? time = null, Uri? frontend = null, ZabbixHttpSettings? http = null, bool allowUnencryptedCredentials = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        var services = new ServiceCollection();
        services.AddSingleton(time ?? TimeProvider.System);            // ahead of AddZabbixClient's TryAdd
        services.AddSingleton<ZabbixPowerShellLoggerProvider>();
        services.AddLogging(logging => ZabbixModuleTelemetry.AddModuleLogging(logging));
        IZabbixClientBuilder server = services.AddZabbixClient(name)
            .WithEndpoint(endpoint)
            .WithCredential(credential)
            .Configure(options =>
            {
                options.FrontendUri = frontend;
                options.AllowUnencryptedCredentials = allowUnencryptedCredentials;
            });
        http?.ApplyTo(server);
        services.AddSingleton<ZabbixPowerShellTelemetry>();
        if (exportTelemetry) ZabbixModuleTelemetry.AddOtlpExport(services);

        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        try
        {
            return new ZabbixConnection(name, endpoint, credential, provider, exportTelemetry);
        }
        catch (Exception) when (Dispose(provider, exportTelemetry))
        {
            throw;
        }
    }

    private static bool Dispose(ServiceProvider provider, bool exportTelemetry)
    {
        provider.Dispose();
        if (exportTelemetry) ZabbixModuleTelemetry.Release();
        return false;
    }

    /// <summary>Takes a lease for one call; <see langword="false"/> once the connection is closed.</summary>
    internal bool TryLease()
    {
        int references = Volatile.Read(ref _references);
        while (references > 0)
        {
            int seen = Interlocked.CompareExchange(ref _references, references + 1, references);
            if (seen == references)
            {
                if (!IsClosed) return true;
                Release();
                return false;
            }
            references = seen;
        }
        return false;
    }

    /// <summary>
    /// The container's disposal, once the last lease is out (<see cref="Task.CompletedTask"/> until then): observable, so
    /// whoever must know the container is gone (a test, a closing process) can wait for it.
    /// </summary>
    internal Task Released { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The client's logoff, once the last lease is out (<see cref="Task.CompletedTask"/> until then): a login session
    /// (<c>-Credential</c>) is logged out (<c>user.logout</c>, best effort) before the container goes.
    /// <c>Disconnect-ZbxServer</c> waits for it, and not for the container, whose disposal can wait on a login a Ctrl+C
    /// left running.
    /// </summary>
    internal Task LoggedOut { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Returns a lease; the last one out logs the client off (<see cref="LoggedOut"/>) and then disposes the container — on a pool
    /// thread, never the caller's: the resilience
    /// registry's dispose waits for an execution still in flight (a version probe or a login a Ctrl+C left running), and
    /// the caller is often the pipeline thread, which must return the prompt at once (otherwise Ctrl+C in
    /// <c>Connect-ZbxServer</c> would hold the prompt for the request's 30 s deadline).
    /// </summary>
    internal void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        // The process's exporters are flushed (or, for the last connection, disposed) here, before the caller returns: a
        // process that exits right after Disconnect-ZbxServer still exports what the connection recorded (a flush waits
        // on the exporters, never on the resilience registry).
        if (_exports) ZabbixModuleTelemetry.Release();
        // The server first, on its own: LogOffAsync ends a login session while the transport it needs is still there. The
        // client is the container's, shared, so the container's disposal then releases it. It never throws.
        Task loggedOut = LogOffAsync();
        LoggedOut = loggedOut;
        Released = DisposeServicesAsync(loggedOut);
    }

    // Both continue on the thread pool at once (ForceYielding), so the caller's thread returns before either runs. A logoff
    // that fails is logged and its session left to expire; the container's release of the client tries it once more.
    private async Task LogOffAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await Client.LogOffAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)       // best effort, as the client's own disposal is
        {
            ZabbixPowerShellLog.LogOffFailed(_log, Name, failure);
        }
    }

    private async Task DisposeServicesAsync(Task loggedOut)
    {
        await loggedOut.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await _services.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Closes the connection: no new call starts on it, and the container is disposed when the last in-flight call returns.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        int inFlight = Volatile.Read(ref _references) - 1;
        ZabbixPowerShellLog.Disconnected(_log, Name, inFlight);
        Release();
    }

    /// <summary>The name and endpoint.</summary>
    public override string ToString() => $"{Name} ({Endpoint})";
}
