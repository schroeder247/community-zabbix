// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>An API token, as created under <i>Users → API tokens</i> or by <c>token.create</c> + <c>token.generate</c>.</summary>
public sealed class ZabbixApiTokenCredential : ZabbixCredential
{
    private readonly string _token;

    /// <summary>A credential over <paramref name="token"/>.</summary>
    /// <param name="token">The token; a secret, never logged.</param>
    public ZabbixApiTokenCredential([ZabbixSecret] string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (!IsWellFormed(token))
            throw new ArgumentException("An API token is one word: it holds no whitespace or control character (a token read from a file keeps its line end).", nameof(token));
        _token = token;
    }

    /// <inheritdoc/>
    public override ValueTask<string?> GetTokenAsync(ZabbixTokenRequestContext context, CancellationToken cancellationToken = default) => new(_token);

    /// <inheritdoc/>
    public override string ToString() => nameof(ZabbixApiTokenCredential);

    /// <summary>Whether <paramref name="token"/> can travel as a bearer: no whitespace and no control character.</summary>
    /// <param name="token">The token as configured.</param>
    /// <returns><see langword="true"/> when the token is one word of printable characters.</returns>
    internal static bool IsWellFormed([ZabbixSecret] string token) => !token.AsSpan().ContainsAny(s_notInAToken) && !token.Any(char.IsControl);

    private static readonly System.Buffers.SearchValues<char> s_notInAToken = System.Buffers.SearchValues.Create(" \t\r\n\v\f\u0085\u00a0\u2028\u2029");
}
