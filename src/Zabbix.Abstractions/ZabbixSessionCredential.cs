// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;

namespace Community.Zabbix;

/// <summary>
/// Represents a user name and password credential: the first request that needs a token logs in (<c>user.login</c>) and
/// the session id is the token from then on.</summary>
/// <remarks>
/// <para>The login happens once however many requests race for it; a caller's cancellation ends only that caller's
/// wait. A session the server has ended is renewed once per refused request. A refused login is answered from memory
/// for <see cref="LoginBackoff"/>, because Zabbix blocks an account for <c>login_block</c> after <c>login_attempts</c>
/// failures and every further failure re-arms the block. A user whose group requires multi-factor authentication
/// cannot log in through the API (the session stays unconfirmed), so the login throws
/// <see cref="System.Security.Authentication.AuthenticationException"/>; use <see cref="ZabbixApiTokenCredential"/>.</para>
/// <para>The password is kept for the credential's lifetime, since it must be able to log in again;
/// <see cref="System.Security.SecureString"/> is not recommended for new development. Where the password should not
/// live in the process between logins, pass a provider: it is called for each login and the value is dropped once the
/// request is written.</para>
/// </remarks>
public sealed class ZabbixSessionCredential : ZabbixCredential
{
    private readonly Func<CancellationToken, ValueTask<string>> _password;
    private readonly Lock _gate = new();
    private Task<string>? _session;
    private string? _issued;
    private Exception? _refusal;
    private long _refusedAt;

    /// <summary>A credential for <paramref name="userName"/> with a password held for its lifetime.</summary>
    /// <param name="userName">The Zabbix user.</param>
    /// <param name="password">The password; a secret, never logged.</param>
    public ZabbixSessionCredential(string userName, [ZabbixSecret] string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrEmpty(password);
        UserName = userName;
        _password = _ => new ValueTask<string>(password);
    }

    /// <summary>A credential for <paramref name="userName"/> whose password is fetched for each login and not kept.</summary>
    /// <param name="userName">The Zabbix user.</param>
    /// <param name="password">Answers the password when a login needs it.</param>
    public ZabbixSessionCredential(string userName, Func<CancellationToken, ValueTask<string>> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);
        UserName = userName;
        _password = password;
    }

    /// <summary>Gets the Zabbix user the credential logs in as.</summary>
    /// <value>The user name; personal data, so it is classified for redaction and never put in a message.</value>
    [ZabbixPersonal]
    public string UserName { get; }

    /// <summary>How long a refused login is answered from memory before the server is asked again; Zabbix's default <c>login_block</c>.</summary>
    public TimeSpan LoginBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    public override ValueTask<string?> GetTokenAsync(ZabbixTokenRequestContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context.Transport);
        ArgumentNullException.ThrowIfNull(context.Time);
        return WaitForSessionAsync(context, cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The password provider and the login request are caller-supplied code; started on the pool, a synchronous prefix of either never holds the first caller, which waits on its own token as every other caller does (decisions.md, \"Async meets a synchronous contract at three places\").")]
    private async ValueTask<string?> WaitForSessionAsync(ZabbixTokenRequestContext context, CancellationToken cancellationToken)
    {
        Task<string> session;
        TaskCompletionSource<string>? login = null;
        lock (_gate)
        {
            if (_refusal is { } refusal && context.Time.GetElapsedTime(_refusedAt) < LoginBackoff)
                throw refusal switch
                {
                    ZabbixApiException api => new ZabbixApiException(api.Code, api.ServerMessage ?? api.Message, api.Detail, api.Method, refusal),
                    _ => new System.Security.Authentication.AuthenticationException(refusal.Message, refusal),
                };
            // Only the login's task is published under the lock: the password provider and the request are unknown
            // code, so they run on the thread pool, and every caller (the first included) waits on the task with its own
            // token instead of on the lock. Its continuations run asynchronously, so one waiter that goes on synchronously
            // into its request does not hold the others behind it.
            if (_session is null || _session.IsFaulted || _session.IsCanceled)
            {
                login = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _session = login.Task;
            }
            session = _session;
        }
        // The caller that started the login waits for it to finish, on its own token, as every other caller does.
        if (login is not null)
            await Task.Run(() => CompleteLoginAsync(login, context), CancellationToken.None).WaitAsync(cancellationToken).ConfigureAwait(false);
        return await session.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Every outcome of the login reaches the waiters through the source, a failure included, as the task's own.
    private async Task CompleteLoginAsync(TaskCompletionSource<string> login, ZabbixTokenRequestContext context)
    {
        Task<string> attempt = LoginAsync(context);
        await ((Task)attempt).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        login.TrySetFromTask(attempt);
    }

    /// <inheritdoc/>
    public override bool Invalidate([ZabbixSecret] string token, ZabbixApiException refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        if (refusal.Kind is not (ZabbixApiErrorKind.SessionTerminated or ZabbixApiErrorKind.NotAuthorized)) return false;
        lock (_gate)
        {
            // Only the session that was refused is dropped: a racing caller may already have logged in again.
            if (string.Equals(_issued, token, StringComparison.Ordinal))
            {
                _session = null;
                _issued = null;
            }
        }
        return true;
    }

    /// <summary>The session the last login issued, while it has not been refused or ended; <see langword="null"/> before a login.</summary>
    internal string? Session
    {
        get
        {
            lock (_gate) return _issued;
        }
    }

    /// <summary>Forgets <paramref name="session"/> after it was logged out, so the next request logs in again.</summary>
    /// <param name="session">The session that was ended.</param>
    internal void Forget([ZabbixSecret] string session)
    {
        lock (_gate)
        {
            if (!string.Equals(_issued, session, StringComparison.Ordinal)) return;
            _session = null;
            _issued = null;
        }
    }

    /// <summary>Returns the credential's type name; the user name is personal data and is read from <see cref="UserName"/>.</summary>
    /// <returns><c>ZabbixSessionCredential</c>.</returns>
    public override string ToString() => nameof(ZabbixSessionCredential);

    // The login's own lifetime: no caller's token reaches it (CancellationToken.None), so the first caller giving up does
    // not fail every other caller waiting on the same login. The HTTP pipeline's attempt and total timeouts bound it.
    private async Task<string> LoginAsync(ZabbixTokenRequestContext context)
    {
        try
        {
            var login = new ZabbixLoginParameters(UserName, await _password(CancellationToken.None).ConfigureAwait(false));
            ZabbixLoginUser user = await context.Transport.SendAsync("user.login", login, ZabbixLoginJson.Default.ZabbixLoginParameters, ZabbixLoginJson.Default.ZabbixLoginUser, CancellationToken.None).ConfigureAwait(false);
            if (user.MfaId is { } mfa and not 0)
            {
                throw new System.Security.Authentication.AuthenticationException(string.Create(CultureInfo.InvariantCulture,
                    $"The Zabbix user must complete multi-factor authentication (MFA method {mfa}), which the API cannot do: the session it was given stays unconfirmed. Use an API token for this user."));
            }
            string session = user.SessionId is { Length: > 0 } id ? id : throw new InvalidDataException("The user.login reply carries no session id.");
            lock (_gate)
            {
                _issued = session;
                _refusal = null;
            }
            return session;
        }
        catch (Exception refusal) when (refusal is ZabbixApiException { Kind: ZabbixApiErrorKind.LoginFailed } or System.Security.Authentication.AuthenticationException)
        {
            lock (_gate)
            {
                _refusal = refusal;
                _refusedAt = context.Time.GetTimestamp();
            }
            throw;
        }
    }
}
