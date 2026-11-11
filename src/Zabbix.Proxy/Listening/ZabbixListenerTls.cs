// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Listening;

/// <summary>What the builders' TLS verbs write to a listener's options: a key as hex text, and the kinds it accepts.</summary>
internal static class ZabbixListenerTls
{
    // A pre-shared key's length, as Zabbix reads a TLSPSKFile: 32 to 512 hex digits.
    private const int MinPskBytes = 16;
    private const int MaxPskBytes = 256;

    /// <summary>The key as the hex text a <c>TLSPSKFile</c> holds and the options keep.</summary>
    /// <param name="key">The key.</param>
    /// <param name="parameter">The caller's parameter, which an exception names.</param>
    /// <returns>The key's lower-case hex digits.</returns>
    /// <exception cref="ArgumentException">The key isn't 16 to 256 bytes.</exception>
    public static string Hex(ReadOnlySpan<byte> key, string parameter)
    {
        if (key.Length is < MinPskBytes or > MaxPskBytes)
            throw new ArgumentException($"A pre-shared key is {MinPskBytes} to {MaxPskBytes} bytes, as Zabbix reads it.", parameter);
        return Convert.ToHexStringLower(key);
    }

    /// <summary>
    /// Makes the listener accept <paramref name="kind"/>: in place of unencrypted connections when they were all it accepted,
    /// its default, and beside the kinds it accepted otherwise.
    /// </summary>
    /// <param name="options">The listener's options.</param>
    /// <param name="kind">The kind its credentials allow.</param>
    public static void Accept(ZabbixListenerOptions options, ZabbixTlsAcceptModes kind)
        => options.TlsAccept = options.TlsAccept == ZabbixTlsAcceptModes.Unencrypted ? kind : options.TlsAccept | kind;
}
