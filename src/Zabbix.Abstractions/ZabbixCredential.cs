// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents how a request proves who it is. The Azure <c>TokenCredential</c> shape: an abstract class (an extension
/// point that may gain members) answering the bearer token a request carries, given the
/// <see cref="ZabbixTokenRequestContext"/> it may log in through. Two are built in: an API token, and a session
/// that logs in with a user name and password.
/// </summary>
public abstract class ZabbixCredential
{
    /// <summary>No credential: requests go out anonymous, so only the methods that need no authentication answer.</summary>
    public static ZabbixCredential Anonymous { get; } = new AnonymousCredential();

    /// <summary>An API token, as created under <i>Users → API tokens</i>: a <see cref="ZabbixApiTokenCredential"/>.</summary>
    /// <param name="token">The token's secret.</param>
    /// <returns>A credential that sends <paramref name="token"/> as the bearer.</returns>
    /// <exception cref="ArgumentException"><paramref name="token"/> is empty, or holds whitespace or a control character.</exception>
    public static ZabbixApiTokenCredential FromApiToken([ZabbixSecret] string token) => new(token);

    /// <summary>
    /// A user name and password that log in when a request first needs a session, and again when the server ends it: a
    /// <see cref="ZabbixSessionCredential"/>.
    /// </summary>
    /// <param name="userName">The Zabbix user.</param>
    /// <param name="password">The user's password, held for the credential's lifetime.</param>
    /// <returns>A credential that logs in as <paramref name="userName"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="userName"/> or <paramref name="password"/> is empty.</exception>
    public static ZabbixSessionCredential FromLogin(string userName, [ZabbixSecret] string password) => new(userName, password);

    /// <summary>The token to send as <c>Authorization: Bearer</c>; <see langword="null"/> to send none.</summary>
    /// <param name="context">The transport a credential may log in through, the method being called, and the clock.</param>
    /// <param name="cancellationToken">Cancels this caller's wait; a login shared with other callers is not cancelled by it.</param>
    /// <returns>The token to send as the bearer, or <see langword="null"/> to send the call without one.</returns>
    public abstract ValueTask<string?> GetTokenAsync(ZabbixTokenRequestContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// The server answered a request that carried <paramref name="token"/> with an API error, of any kind. When
    /// <paramref name="refusal"/> says it refused the token before running the method
    /// (<see cref="ZabbixApiErrorKind.SessionTerminated"/> or <see cref="ZabbixApiErrorKind.NotAuthorized"/>), a credential that
    /// can obtain a fresh token forgets this one and answers <see langword="true"/>: the transport then asks again and re-sends
    /// the request <b>once</b>; a second refusal is the caller's. Re-sending is safe even for a write in those two kinds, because
    /// authentication runs before the method (<c>CLocalApiClient.php</c>: <c>authenticate</c> precedes the call). You must answer
    /// <see langword="false"/> for any other kind, or the transport re-sends a write the server may have run. The default answers
    /// <see langword="false"/>: an API token that the server no longer knows cannot be renewed by the client.
    /// </summary>
    /// <param name="token">The token that was refused.</param>
    /// <param name="refusal">The server's answer.</param>
    /// <returns>Whether the next <see cref="GetTokenAsync"/> will answer a different token.</returns>
    public virtual bool Invalidate([ZabbixSecret] string token, ZabbixApiException refusal) => false;

    private sealed class AnonymousCredential : ZabbixCredential
    {
        public override ValueTask<string?> GetTokenAsync(ZabbixTokenRequestContext context, CancellationToken cancellationToken = default) => new((string?)null);
    }
}
