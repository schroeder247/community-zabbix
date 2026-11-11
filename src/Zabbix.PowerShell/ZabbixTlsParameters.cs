// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Builds the SDK's <see cref="ZabbixTlsOptions"/> from a command's TLS parameters (<see cref="IZabbixTlsParameters"/>),
/// one rule for every wire command: a connecting command's mode is <c>-Tls</c>, or, when omitted, the kind of credential
/// given; a listener accepts <c>-TlsAccept</c>, or, when omitted, the kinds of credential given. The pre-shared key
/// leaves its <see cref="System.Security.SecureString"/> only here, into the options' own classified
/// <see cref="ZabbixTlsOptions.Psk"/>.
/// </summary>
internal static class ZabbixTlsParameters
{
    /// <summary>
    /// The TLS words of a connecting command (<c>TLSConnect</c>): <see langword="null"/> when no TLS parameter was given,
    /// so the caller's default holds (unencrypted, or a connection's own).
    /// </summary>
    /// <param name="words">The command's TLS parameters.</param>
    /// <param name="mode">The command's <c>-Tls</c>, if given.</param>
    /// <exception cref="ZabbixParameterException">Credentials of both kinds are given without <c>-Tls</c> to choose.</exception>
    public static ZabbixTlsOptions? Connecting(IZabbixTlsParameters words, ZabbixTlsMode? mode)
    {
        ArgumentNullException.ThrowIfNull(words);
        (bool psk, bool certificate) = Kinds(words);
        if (mode is null && !psk && !certificate) return null;
        ZabbixTlsMode chosen = mode ?? (psk, certificate) switch
        {
            (true, false) => ZabbixTlsMode.PreSharedKey,
            (false, true) => ZabbixTlsMode.Certificate,
            _ => throw new ZabbixParameterException("ZabbixTlsModeAmbiguous", "Tls",
                "Both a pre-shared key and a certificate are given: say which the connection uses with -Tls PreSharedKey or -Tls Certificate."),
        };
        return Options(words, chosen);
    }

    /// <summary>
    /// The TLS words of a listener (<c>TLSAccept</c>): the modes accepted, <paramref name="accept"/> when given, else
    /// those the credentials given allow (unencrypted when none is given), and the credentials, whose mode stays
    /// <see cref="ZabbixTlsMode.Unencrypted"/> as a listener's options require.
    /// </summary>
    /// <param name="words">The command's TLS parameters.</param>
    /// <param name="accept">The command's <c>-TlsAccept</c>, if given.</param>
    public static (ZabbixTlsAcceptModes Accept, ZabbixTlsOptions? Tls) Listening(IZabbixTlsParameters words, IReadOnlyCollection<ZabbixTlsAcceptModes>? accept)
    {
        ArgumentNullException.ThrowIfNull(words);
        (bool psk, bool certificate) = Kinds(words);
        ZabbixTlsAcceptModes modes = accept is { Count: > 0 }
            ? accept.Aggregate((ZabbixTlsAcceptModes)0, (mask, mode) => mask | mode)
            : (psk ? ZabbixTlsAcceptModes.Psk : 0) | (certificate ? ZabbixTlsAcceptModes.Certificate : 0);
        if (modes == 0) modes = ZabbixTlsAcceptModes.Unencrypted;
        return (modes, psk || certificate ? Options(words, ZabbixTlsMode.Unencrypted) : null);
    }

    /// <summary>
    /// Checks <paramref name="tls"/> as the connection that uses it will: by building the SDK's
    /// <see cref="ZabbixWireLink"/> over it, which validates the words and, in certificate mode, reads the files and parses
    /// the pins, so a command that only records the words (<c>Connect-ZbxServer</c>) fails when it is given them, not at
    /// the first command that connects. Nothing is sent: a link connects only when it is asked to exchange.
    /// </summary>
    /// <param name="tls">The words.</param>
    /// <param name="time">The clock certificate validity is judged on.</param>
    /// <exception cref="ArgumentException">The words do not validate.</exception>
    /// <exception cref="IOException">A certificate, key, CA or CRL file cannot be read.</exception>
    public static void Check(ZabbixTlsOptions tls, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(tls);
        using var probe = new ZabbixWireLink([new DnsEndPoint("localhost", ZabbixWireClient.DefaultServerPort)], new ZabbixConnectionOptions { Tls = tls }, loggers: null, time);
    }

    /// <summary>
    /// Whether <paramref name="failure"/> is how building a connection over TLS words fails before anything is sent:
    /// words that do not validate (<see cref="ArgumentException"/>, <see cref="OptionsValidationException"/>), or a
    /// certificate, key, CA or CRL file that cannot be read or is not what it should be (<see cref="IOException"/>,
    /// <see cref="InvalidDataException"/>, <see cref="System.Security.Cryptography.CryptographicException"/>).
    /// </summary>
    /// <param name="failure">What building the connection threw.</param>
    public static bool IsTlsFailure(Exception failure)
        => failure is ArgumentException or OptionsValidationException or IOException or InvalidDataException
            or System.Security.Cryptography.CryptographicException;

    private static (bool Psk, bool Certificate) Kinds(IZabbixTlsParameters words)
        => (words.PskIdentity is not null || words.Psk is not null,
            (words.Certificate ?? words.CertificateKey ?? words.CertificateAuthority ?? words.CertificateRevocationList ?? words.ServerIssuer ?? words.ServerSubject) is not null);

    // NetworkCredential is the BCL's SecureString → string conversion; the string lives only in the classified option.
    private static ZabbixTlsOptions Options(IZabbixTlsParameters words, ZabbixTlsMode mode) => new()
    {
        Mode = mode,
        PskIdentity = words.PskIdentity,
        Psk = words.Psk is null ? null : new NetworkCredential(string.Empty, words.Psk).Password,
        CertificateFile = words.Certificate,
        KeyFile = words.CertificateKey,
        CaFile = words.CertificateAuthority,
        CrlFile = words.CertificateRevocationList,
        PeerCertificateIssuer = words.ServerIssuer,
        PeerCertificateSubject = words.ServerSubject,
    };
}
