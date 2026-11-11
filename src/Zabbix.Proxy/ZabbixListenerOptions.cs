// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Community.Zabbix.Listening;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Represents a listener's options: an address, its limits, who may connect, and which protections it accepts.
/// </summary>
/// <remarks>
/// One options type for every listener: a trapper's, a push agent's and a proxy's alike, the passive agent's adding its own on
/// top. Like <c>ConsoleFormatterOptions</c>, it is a base that is also used as it is (a bare listener, a trapper), and the role
/// options that extend it are sealed. The builders' <c>AllowPeer</c> and <c>AllowPsk</c> write it. It is read once when the
/// listener starts: a configuration reload does not rebind a port or change a running listener's peers.
/// </remarks>
public class ZabbixListenerOptions : IValidatableObject
{
    // Upstream's HOST_TLS_PSK_IDENTITY_LEN (include/zbxcommon.h, 7.0/7.4/8.0 alike), as ZabbixTlsOptions holds it.
    private const int MaxPskIdentityBytes = 512;

    /// <summary>The address to bind; every interface by default.</summary>
    public string Address { get; set; } = "0.0.0.0";

    /// <summary>The port; <c>0</c> lets the platform pick one (tests).</summary>
    [Range(0, 65535)]
    public int Port { get; set; } = ZabbixServerNode.DefaultPort;

    /// <summary>How many connections are handled at once; the rest wait in the accept queue.</summary>
    [Range(1, 65536)]
    public int MaxConcurrentConnections { get; set; } = 256;

    /// <summary>How many connections one peer address may hold at once; beyond it the connection is refused, not queued.</summary>
    [Range(1, 65536)]
    public int MaxConnectionsPerPeer { get; set; } = 16;

    /// <summary>
    /// Who may connect: IP addresses, CIDR blocks and DNS names, one per entry or comma-separated as Zabbix's
    /// <c>Server=</c> line writes them (see <see cref="ZabbixAllowedPeers"/>). <b>Required</b>: an empty list fails
    /// validation, as upstream refuses it; say <c>0.0.0.0/0</c> and <c>::/0</c> to allow everyone.
    /// </summary>
    public IList<string> AllowedPeers { get; } = [];

    /// <summary>How long one request may take, connect to reply.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The largest frame accepted.</summary>
    [Range(1L, 17_179_869_184L)]
    public long MaxPayloadBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Whether replies are compressed.</summary>
    public bool Compress { get; set; }

    /// <summary>
    /// Which protections a connection may arrive with, as a set: Zabbix's <c>TLSAccept</c> (<c>unencrypted</c>,
    /// <c>psk</c>, <c>cert</c>, the wire values 1, 2 and 4 the API's <c>tls_accept</c> uses). Each connection is checked
    /// against it: its first byte says TLS or not (upstream <c>zbx_tcp_accept</c>), and a TLS connection's hello says
    /// pre-shared key or certificate.
    /// </summary>
    public ZabbixTlsAcceptModes TlsAccept { get; set; } = ZabbixTlsAcceptModes.Unencrypted;

    /// <summary>
    /// The credentials the accepted TLS kinds use: the certificate and key files (and the CA and pins) for <c>cert</c>,
    /// the identity and key for <c>psk</c>. Its <see cref="ZabbixTlsOptions.Mode"/> is the connecting side's word and
    /// must stay <see cref="ZabbixTlsMode.Unencrypted"/> here; <see cref="TlsAccept"/> decides.
    /// </summary>
    public ZabbixTlsOptions? Tls { get; set; }

    /// <summary>
    /// More PSK identities than <see cref="Tls"/>'s one, identity to hex key: a server-side trapper answers each host by
    /// its own identity, as upstream looks the offered identity up (<c>zbx_psk_server_cb</c>). The keys are secrets.
    /// </summary>
    [ZabbixSecret]
    public IDictionary<string, string> PreSharedKeys { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The endpoint the options describe.</summary>
    public IPEndPoint ToEndPoint() => new(IPAddress.Parse(Address), Port);

    /// <summary>The allowed peers, parsed; <see cref="FormatException"/> when the list is empty or an entry is not a peer.</summary>
    public ZabbixAllowedPeers ParseAllowedPeers() => ZabbixAllowedPeers.Parse(string.Join(',', AllowedPeers), null);

    /// <summary>
    /// The rules the attributes cannot say: a parseable address, a non-empty allowed-peer list, a known accept set, and
    /// credentials for every TLS kind accepted. Run by the generated validator at start (<c>ValidateOnStart</c>) and by
    /// the listener itself before it binds.
    /// </summary>
    /// <param name="validationContext">The context.</param>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => [.. ValidateEndpoint(), .. ValidateAcceptSet(), .. ValidateCredentialPairs(), .. ValidatePskTable()];

    // The address, the deadline and who may connect.
    private IEnumerable<ValidationResult> ValidateEndpoint()
    {
        if (Hosting.ZabbixOptionsRanges.Duration(Timeout, nameof(Timeout)) is { } timeout) yield return timeout;
        if (!IPAddress.TryParse(Address, out _))
            yield return new ValidationResult($"Address \"{Address}\" is not an IP address", [nameof(Address)]);
        if (!ZabbixAllowedPeers.TryParse(string.Join(',', AllowedPeers), out _, out string? peers))
            yield return new ValidationResult(peers, [nameof(AllowedPeers)]);                  // the validator names the member
    }

    // What the listener accepts, and the certificate the cert mode needs.
    private IEnumerable<ValidationResult> ValidateAcceptSet()
    {
        const ZabbixTlsAcceptModes known = ZabbixTlsAcceptModes.Unencrypted | ZabbixTlsAcceptModes.Psk | ZabbixTlsAcceptModes.Certificate;
        if (TlsAccept == 0 || (TlsAccept & ~known) != 0)
            yield return new ValidationResult($"TlsAccept {(int)TlsAccept} is not a set of unencrypted (1), psk (2) and cert (4)", [nameof(TlsAccept)]);
        if (Tls is { Mode: not ZabbixTlsMode.Unencrypted })
            yield return new ValidationResult("Tls.Mode is the connecting side's word; a listener says what it accepts with TlsAccept", [nameof(Tls)]);
        if (TlsAccept.HasFlag(ZabbixTlsAcceptModes.Certificate) && (Tls?.CertificateFile is null || Tls.KeyFile is null))
            yield return new ValidationResult("TlsAccept includes cert: Tls.CertificateFile and Tls.KeyFile are required", [nameof(Tls)]);
    }

    // Upstream's dependencies (tls_validate.c): a key and its certificate, a PSK and its identity, come in pairs; a CRL, an
    // issuer or a subject only with a certificate. And credentials a listener will never use are a configuration that does not
    // do what it says (a PSK set while TlsAccept stays unencrypted): refused, where upstream refuses them only when TlsAccept is
    // absent.
    private IEnumerable<ValidationResult> ValidateCredentialPairs() => [.. ValidateCertificatePair(), .. ValidatePskPair()];

    // The certificate's companions need the certificate, and the certificate needs the cert mode.
    private IEnumerable<ValidationResult> ValidateCertificatePair()
    {
        if (Tls is { CertificateFile: null } && HasCertificateCompanion(Tls))
            yield return new ValidationResult("Tls.KeyFile, CaFile, CrlFile, PeerCertificateIssuer and PeerCertificateSubject are used only with Tls.CertificateFile", [nameof(Tls)]);
        if (!TlsAccept.HasFlag(ZabbixTlsAcceptModes.Certificate) && Tls?.CertificateFile is not null)
            yield return new ValidationResult("Tls.CertificateFile is set but TlsAccept does not include cert: the certificate would never be used", [nameof(Tls), nameof(TlsAccept)]);
    }

    // The identity and the key come together, and either needs the psk mode.
    private IEnumerable<ValidationResult> ValidatePskPair()
    {
        if (string.IsNullOrEmpty(Tls?.PskIdentity) != string.IsNullOrEmpty(Tls?.Psk))
            yield return new ValidationResult("Tls.PskIdentity and Tls.Psk are set together or not at all", [nameof(Tls)]);
        if (!TlsAccept.HasFlag(ZabbixTlsAcceptModes.Psk) && HasAnyPsk())
            yield return new ValidationResult("a pre-shared key is set but TlsAccept does not include psk: the key would never be used", [nameof(Tls), nameof(PreSharedKeys), nameof(TlsAccept)]);
    }

    private static bool HasCertificateCompanion(ZabbixTlsOptions tls)
        => (tls.KeyFile ?? tls.CrlFile ?? tls.PeerCertificateIssuer ?? tls.PeerCertificateSubject ?? tls.CaFile) is not null;

    private bool HasAnyPsk() => !string.IsNullOrEmpty(Tls?.PskIdentity) || !string.IsNullOrEmpty(Tls?.Psk) || PreSharedKeys.Count > 0;

    // Every identity and key the psk mode answers with.
    private IEnumerable<ValidationResult> ValidatePskTable()
    {
        if (!TlsAccept.HasFlag(ZabbixTlsAcceptModes.Psk)) yield break;
        IReadOnlyList<KeyValuePair<string, string>> keys = PskTable();
        if (keys.Count == 0)
            yield return new ValidationResult("TlsAccept includes psk: Tls.PskIdentity and Tls.Psk, or PreSharedKeys, are required", [nameof(Tls), nameof(PreSharedKeys)]);
        foreach ((string identity, string key) in keys)
        {
            if (string.IsNullOrEmpty(identity))
                yield return new ValidationResult("a PSK identity is never empty", [nameof(PreSharedKeys)]);
            else if (Encoding.UTF8.GetByteCount(identity) > MaxPskIdentityBytes)
                yield return new ValidationResult($"a PSK identity is at most {MaxPskIdentityBytes} bytes of UTF-8, as upstream reads it", [nameof(PreSharedKeys)]);
            else if (!IsPsk(key))
                yield return new ValidationResult($"the PSK for identity \"{identity}\" is not an even number of hex digits, 32 to 512", [nameof(PreSharedKeys)]);
        }
    }

    /// <summary>Every identity this listener answers, <see cref="Tls"/>'s one first.</summary>
    internal IReadOnlyList<KeyValuePair<string, string>> PskTable()
    {
        var table = new List<KeyValuePair<string, string>>(PreSharedKeys.Count + 1);
        if (!string.IsNullOrEmpty(Tls?.PskIdentity) && !string.IsNullOrEmpty(Tls.Psk)) table.Add(new(Tls.PskIdentity, Tls.Psk));
        table.AddRange(PreSharedKeys);
        return table;
    }

    private static bool IsPsk(string hex)
    {
        try
        {
            CryptographicOperations.ZeroMemory(ZabbixTls.ParsePsk(hex).Span);
            return true;
        }
        catch (Exception failure) when (failure is FormatException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>The generated validator for <see cref="ZabbixListenerOptions"/>.</summary>
[OptionsValidator]
internal sealed partial class ZabbixListenerOptionsValidator : IValidateOptions<ZabbixListenerOptions>
{
}
