// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Community.Zabbix.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix;

/// <summary>
/// Provides the verbs of a wire client's builder, the same on the builder <see cref="ZabbixWireClient.CreateBuilder"/> returns
/// and on the one <see cref="ZabbixWireClientServiceCollectionExtensions.AddZabbixWireClient(IServiceCollection, string)"/>
/// returns. Each verb writes the client's named options, which apply in the order of the calls, and returns the builder it
/// was called on.
/// </summary>
/// <remarks>
/// The options keep the text that configuration spells, a server as <c>host:port</c> and a key as hex digits, so
/// <see cref="Bind"/> can fill them. The verbs take the .NET types and write that text.
/// </remarks>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
public static class ZabbixWireClientBuilderExtensions
{
    // A pre-shared key's length, as Zabbix reads a TLSPSKFile: 32 to 512 hex digits.
    private const int MinPskBytes = 16;
    private const int MaxPskBytes = 256;

    extension<TBuilder>(TBuilder builder) where TBuilder : IZabbixWireClientBuilder
    {
        /// <summary>
        /// Sets the server or proxy the client talks to, in Zabbix's spelling: one node, or the nodes of one HA cluster joined
        /// by <c>;</c>, tried in order. The client moves to the next node only when it can't connect to one. A client has one
        /// server, so a later call replaces it.
        /// </summary>
        /// <param name="server">
        /// The server: <c>host</c> or <c>host:port</c>, on port 10051 when it names none, or the nodes of one HA cluster:
        /// <c>"zbx-1;zbx-2"</c>. An IPv6 address with a port is bracketed: <c>[::1]:10051</c>.
        /// </param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="server"/> is empty, isn't a node, names a node twice, or names more than one server (<c>,</c>).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
        public TBuilder WithServer(string server)
        {
            ArgumentNullException.ThrowIfNull(builder);
            string text = ZabbixServerActiveText.Server(server, nameof(server));
            builder.Services.Configure<ZabbixWireClientOptions>(builder.Name, options => options.Server = text);
            return builder;
        }

        /// <summary>
        /// Sets the server or proxy the client talks to: one node, or the nodes of one HA cluster, tried in order. A client has
        /// one server, so a later call replaces it.
        /// </summary>
        /// <param name="nodes">The server's nodes, in failover order: their host names or IP addresses, and their ports.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException">No node, a node named twice, or a node that isn't a host and port.</exception>
        /// <example>
        /// <code language="csharp">
        /// builder.WithServer(new DnsEndPoint("zbx-1", 10051), new DnsEndPoint("zbx-2", 10051));
        /// </code>
        /// </example>
        public TBuilder WithServer(params ReadOnlySpan<DnsEndPoint> nodes)
        {
            ArgumentNullException.ThrowIfNull(builder);
            string text = ZabbixServerActiveText.Destination(nodes, nameof(nodes));
            builder.Services.Configure<ZabbixWireClientOptions>(builder.Name, options => options.Server = text);
            return builder;
        }

        /// <summary>
        /// Sets the API token that the server's requests carry: the ping, the status, the queue, and the tests. A
        /// <c>user.login</c> session's id works too. The token is never logged.
        /// </summary>
        /// <param name="token">An API token, from <em>Users, API tokens</em> in the frontend.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="token"/> is empty.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="token"/> is <see langword="null"/>.</exception>
        public TBuilder WithToken([ZabbixSecret] string token)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(token);
            builder.Services.Configure<ZabbixWireClientOptions>(builder.Name, options => options.SessionId = token);
            return builder;
        }

        /// <summary>
        /// Encrypts every connection with a pre-shared key: to the server, and to each agent the client reads from. A later
        /// <see cref="WithPsk"/> or <see cref="WithCertificate"/> replaces it.
        /// </summary>
        /// <param name="identity">The key's identity, as the peer has it (<c>TLSPSKIdentity</c>).</param>
        /// <param name="key">
        /// The key, 16 to 256 bytes: what Zabbix's <c>TLSPSKFile</c> holds as hex digits. The options keep it as that text.
        /// </param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="identity"/> is empty, or <paramref name="key"/> isn't 16 to 256 bytes.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
        public TBuilder WithPsk(string identity, [ZabbixSecret] ReadOnlySpan<byte> key)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrEmpty(identity);
            if (key.Length is < MinPskBytes or > MaxPskBytes)
                throw new ArgumentException($"A pre-shared key is {MinPskBytes} to {MaxPskBytes} bytes, as Zabbix reads it.", nameof(key));
            string hex = Convert.ToHexStringLower(key);
            builder.Services.Configure<ZabbixWireClientOptions>(builder.Name, options =>
                options.Connection.Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = identity, Psk = hex });
            return builder;
        }

        /// <summary>
        /// Encrypts every connection with certificates, read from their files at the client's first request: to the server, and
        /// to each agent the client reads from. A later <see cref="WithPsk"/> or <see cref="WithCertificate"/> replaces it.
        /// </summary>
        /// <param name="certificateFile">This end's certificate, PEM (<c>TLSCertFile</c>).</param>
        /// <param name="keyFile">Its private key, PEM (<c>TLSKeyFile</c>).</param>
        /// <param name="caFile">The certificate authorities to trust, PEM (<c>TLSCAFile</c>): the only trust anchor.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException">A file name is empty.</exception>
        /// <exception cref="ArgumentNullException">A file name is <see langword="null"/>.</exception>
        public TBuilder WithCertificate(string certificateFile, string keyFile, string caFile)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(certificateFile);
            ArgumentException.ThrowIfNullOrWhiteSpace(keyFile);
            ArgumentException.ThrowIfNullOrWhiteSpace(caFile);
            builder.Services.Configure<ZabbixWireClientOptions>(builder.Name, options => options.Connection.Tls = new ZabbixTlsOptions
            {
                Mode = ZabbixTlsMode.Certificate,
                CertificateFile = certificateFile,
                KeyFile = keyFile,
                CaFile = caFile,
            });
            return builder;
        }

        /// <summary>Sets how long one attempt on one node may take: connect, handshake, request, and reply.</summary>
        /// <param name="timeout">The longest an attempt may take; 10 seconds unless set.</param>
        /// <returns>The builder.</returns>
        public TBuilder WithTimeout(TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.Configure<ZabbixWireClientOptions>(builder.Name, options => options.Connection.Timeout = timeout);
            return builder;
        }

        /// <summary>Sets whether the client compresses a request when that makes it smaller.</summary>
        /// <param name="enabled">Whether to compress; <see langword="false"/> unless set.</param>
        /// <returns>The builder.</returns>
        public TBuilder WithCompression(bool enabled = true)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.Configure<ZabbixWireClientOptions>(builder.Name, options => options.Connection.Compress = enabled);
            return builder;
        }

        /// <summary>Sets how many values one frame carries at most.</summary>
        /// <param name="batchSize">The most values in one frame; <see cref="ZabbixWireClientOptions.DefaultBatchSize"/> unless set.</param>
        /// <returns>The builder.</returns>
        public TBuilder WithBatchSize(int batchSize)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.Configure<ZabbixWireClientOptions>(builder.Name, options => options.BatchSize = batchSize);
            return builder;
        }

        /// <summary>Sets how long an agent may take to answer a read, which the request carries.</summary>
        /// <param name="timeout">Whole seconds, 1 to 600; 3 seconds, Zabbix's default, unless set.</param>
        /// <returns>The builder.</returns>
        public TBuilder WithCheckTimeout(TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.Configure<ZabbixWireClientOptions>(builder.Name, options => options.CheckTimeout = timeout);
            return builder;
        }

        /// <summary>Sets any of the client's options, in order with the other verbs.</summary>
        /// <param name="configure">Sets the options.</param>
        /// <returns>The builder.</returns>
        /// <example>
        /// <code language="csharp">
        /// builder.Configure(options => options.MaxBatchBytes = 16 * 1024 * 1024);
        /// </code>
        /// </example>
        public TBuilder Configure(Action<ZabbixWireClientOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            builder.Services.Configure(builder.Name, configure);
            return builder;
        }

        /// <summary>
        /// Binds the client's options to a configuration section, in order with the other verbs: <c>Server</c>,
        /// <c>SessionId</c>, <c>BatchSize</c>, <c>MaxBatchBytes</c>, <c>CheckTimeout</c>, and a <c>Connection</c> subsection, as
        /// <see cref="ZabbixWireClientOptions"/> names them. The section is read when the client is built.
        /// </summary>
        /// <param name="section">The client's section.</param>
        /// <returns>The builder.</returns>
        public TBuilder Bind(IConfiguration section)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(section);
            builder.Services.AddOptions<ZabbixWireClientOptions>(builder.Name).Bind(section);
            return builder;
        }
    }
}
