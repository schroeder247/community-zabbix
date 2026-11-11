// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Community.Zabbix.Listening;

namespace Community.Zabbix;

/// <summary>
/// Provides the verbs that say who may connect to a listener, the same on every builder of something that listens:
/// <see cref="ZabbixListener.CreateBuilder"/>'s, a passive agent's, and a proxy's, whose listener is its trapper. Each verb
/// writes the listener's options, in order with the builder's other verbs, and returns the builder it was called on.
/// </summary>
/// <remarks>
/// The options keep the text that configuration spells, Zabbix's <c>Server=</c> line for who may connect and a key as hex
/// digits, so a configuration section can fill them. The verbs take the .NET types and write that text.
/// </remarks>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
public static class ZabbixListenerBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IZabbixListenerBuilder
    {
        /// <summary>
        /// Lets the addresses of <paramref name="network"/> connect, as a CIDR block on Zabbix's <c>Server=</c> line does. A
        /// <see langword="default"/> network is every IPv4 address, <c>0.0.0.0/0</c>, as .NET reads it.
        /// </summary>
        /// <param name="network">The network: <c>IPNetwork.Parse("10.0.0.0/8")</c>.</param>
        /// <returns>The builder.</returns>
        public TBuilder AllowPeer(IPNetwork network) => builder.AllowPeerText(network.ToString());

        /// <summary>Lets <paramref name="address"/> connect, as an address on Zabbix's <c>Server=</c> line does.</summary>
        /// <param name="address">The peer's address. An IPv4 address also matches its IPv4-mapped IPv6 form.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
        public TBuilder AllowPeer(IPAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);
            return builder.AllowPeerText(address.ToString());
        }

        /// <summary>
        /// Lets the addresses <paramref name="hostName"/> resolves to connect, as a DNS name on Zabbix's <c>Server=</c> line
        /// does. The name is resolved at each connection, and one that doesn't resolve matches nothing.
        /// </summary>
        /// <param name="hostName">The peer's DNS name: <c>monitor.example</c>.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="hostName"/> isn't a DNS name: pass an <see cref="IPAddress"/> or an <see cref="IPNetwork"/> for an address.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="hostName"/> is <see langword="null"/>.</exception>
        public TBuilder AllowPeer(string hostName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
            if (Uri.CheckHostName(hostName) != UriHostNameType.Dns)
                throw new ArgumentException($"\"{hostName}\" isn't a DNS name: pass an IPAddress or an IPNetwork for an address.", nameof(hostName));
            return builder.AllowPeerText(hostName);
        }

        /// <summary>
        /// Lets a peer that presents <paramref name="identity"/> connect over TLS with <paramref name="key"/>, as an entry of the
        /// listener's <see cref="ZabbixListenerOptions.PreSharedKeys"/>: a trapper takes each sending host by its own identity.
        /// A listener that accepted only unencrypted connections, as it does unless set, then accepts pre-shared-key ones
        /// only; one that accepted more kinds keeps them. A later call for the same identity replaces its key.
        /// </summary>
        /// <param name="identity">The identity the peer presents (its <c>TLSPSKIdentity</c>).</param>
        /// <param name="key">
        /// The key, 16 to 256 bytes: what the peer's <c>TLSPSKFile</c> holds as hex digits. The options keep it as that text.
        /// </param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="identity"/> is empty, or <paramref name="key"/> isn't 16 to 256 bytes.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
        public TBuilder AllowPsk(string identity, [ZabbixSecret] ReadOnlySpan<byte> key)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrEmpty(identity);
            string hex = ZabbixListenerTls.Hex(key, nameof(key));
            builder.ConfigureListener(options =>
            {
                options.PreSharedKeys[identity] = hex;
                ZabbixListenerTls.Accept(options, ZabbixTlsAcceptModes.Psk);
            });
            return builder;
        }

        // One entry of the Server= line the options keep.
        private TBuilder AllowPeerText(string peer)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.ConfigureListener(options => options.AllowedPeers.Add(peer));
            return builder;
        }
    }
}
