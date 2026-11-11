// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix;

/// <summary>
/// Builds a <see cref="ZabbixClient"/> with more than an address and a token to set. A simple client needs no builder:
/// <c>new ZabbixClient("https://zabbix.example/", token)</c>.
/// </summary>
/// <example>
/// <code language="csharp">
/// await using ZabbixClient zabbix = new ZabbixClientBuilder()
///     .WithEndpoint("https://zabbix.example/")
///     .WithToken(token)
///     .WithMaxConnections(20)
///     .WithLogging(loggers)
///     .Build();
/// </code>
/// </example>
public sealed class ZabbixClientBuilder
{
    private readonly ZabbixOptions _options = new();
    private Uri? _endpoint;
    private ZabbixCredential? _credential;
    private ILoggerFactory? _loggers;
    private TimeProvider? _time;

    /// <summary>Sets the server's address.</summary>
    /// <param name="endpoint">The frontend's address, e.g. <c>https://zabbix.example/</c>, or the JSON-RPC endpoint.</param>
    /// <returns>This builder, to chain the next call.</returns>
    public ZabbixClientBuilder WithEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoint = endpoint;
        return this;
    }

    /// <summary>Sets the server's address.</summary>
    /// <param name="endpoint">The frontend's address, e.g. <c>https://zabbix.example/</c>, or the JSON-RPC endpoint.</param>
    /// <returns>This builder, to chain the next call.</returns>
    /// <exception cref="UriFormatException"><paramref name="endpoint"/> isn't an absolute address.</exception>
    public ZabbixClientBuilder WithEndpoint(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return WithEndpoint(new Uri(endpoint, UriKind.Absolute));
    }

    /// <summary>Sends an API token (<em>Users, API tokens</em> in the frontend) with every call.</summary>
    /// <param name="token">The API token to send.</param>
    /// <returns>This builder, to chain the next call.</returns>
    public ZabbixClientBuilder WithToken([ZabbixSecret] string token)
    {
        _credential = ZabbixCredential.FromApiToken(token);
        return this;
    }

    /// <summary>Logs in with a user's name and password on the first call.</summary>
    /// <param name="login">The user's name and password; only those two are read.</param>
    /// <returns>This builder, to chain the next call.</returns>
    public ZabbixClientBuilder WithLogin(NetworkCredential login)
    {
        ArgumentNullException.ThrowIfNull(login);
        _credential = ZabbixCredential.FromLogin(login.UserName, login.Password);
        return this;
    }

    /// <summary>Authenticates with your own credential, such as one that reads a vault.</summary>
    /// <param name="credential">The credential to ask for each call's token.</param>
    /// <returns>This builder, to chain the next call.</returns>
    public ZabbixClientBuilder WithCredential(ZabbixCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        _credential = credential;
        return this;
    }

    /// <summary>Lets a credential go over plain <c>http</c> to a host that isn't a loopback host.</summary>
    /// <returns>This builder, to chain the next call.</returns>
    public ZabbixClientBuilder AllowUnencryptedCredentials()
    {
        _options.AllowUnencryptedCredentials = true;
        return this;
    }

    /// <summary>Sets how many connections the client opens to the server at once.</summary>
    /// <param name="maxConnections">The most connections to open.</param>
    /// <returns>This builder, to chain the next call.</returns>
    public ZabbixClientBuilder WithMaxConnections(int maxConnections)
    {
        _options.MaxConnectionsPerServer = maxConnections;
        return this;
    }

    /// <summary>Sets the longest one reply may be.</summary>
    /// <param name="maxBytes">The most bytes to read from a reply.</param>
    /// <returns>This builder, to chain the next call.</returns>
    public ZabbixClientBuilder WithMaxResponseSize(long maxBytes)
    {
        _options.MaxResponseContentBytes = maxBytes;
        return this;
    }

    /// <summary>Sets how long calls to one API method may take.</summary>
    /// <param name="method">The method's name, e.g. <c>history.get</c>.</param>
    /// <param name="timeout">The longest a call may take.</param>
    /// <returns>This builder, to chain the next call.</returns>
    public ZabbixClientBuilder WithTimeout(string method, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        _options.Timeouts[method] = timeout;
        return this;
    }

    /// <summary>Sets the frontend's address, where it isn't the API's.</summary>
    /// <param name="frontend">The address the frontend's pages open at.</param>
    /// <returns>This builder, to chain the next call.</returns>
    public ZabbixClientBuilder WithFrontend(Uri frontend)
    {
        _options.FrontendUri = frontend;
        return this;
    }

    /// <summary>Sets where the client logs.</summary>
    /// <param name="loggers">The application's logger factory.</param>
    /// <returns>This builder, to chain the next call.</returns>
    public ZabbixClientBuilder WithLogging(ILoggerFactory loggers)
    {
        _loggers = loggers;
        return this;
    }

    /// <summary>Sets the clock the client's deadlines run on.</summary>
    /// <param name="time">The clock to read.</param>
    /// <returns>This builder, to chain the next call.</returns>
    public ZabbixClientBuilder WithTimeProvider(TimeProvider time)
    {
        _time = time;
        return this;
    }

    /// <summary>Builds the client.</summary>
    /// <returns>The client, which you must dispose.</returns>
    /// <exception cref="InvalidOperationException"><see cref="WithEndpoint(Uri)"/> wasn't called.</exception>
    /// <exception cref="Microsoft.Extensions.Options.OptionsValidationException">What was set doesn't validate.</exception>
    public ZabbixClient Build()
        => new(
            _endpoint ?? throw new InvalidOperationException($"Call {nameof(WithEndpoint)} before {nameof(Build)}."),
            _credential ?? ZabbixCredential.Anonymous,
            _options,
            _loggers,
            _time);
}
