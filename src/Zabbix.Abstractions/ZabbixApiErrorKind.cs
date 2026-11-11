// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Specifies what a Zabbix API <c>error</c> object means, classified from its code and the start of its <c>data</c>
/// text. The codes are the same in 7.0, 7.4 and 8.0 (<c>CJsonRpc.php</c>
/// <c>$_zbx2jsonErrors</c>); the text is always English, because API mode calls <c>initLocales('en_us')</c> before any
/// request is served (<c>ZBase.php</c> 7.0:236, 8.0:237), so matching a prefix is exact, not a guess at a locale.
/// </summary>
public enum ZabbixApiErrorKind
{
    /// <summary>Not classified: an error built without a server error object (code 0), or a code Zabbix does not send.</summary>
    Unknown = 0,

    /// <summary>The request was refused as written: bad parameters, a malformed request (-32600, -32700), or any -32602/-32500 no other kind names.</summary>
    Validation,

    /// <summary><c>Not authorized.</c> (-32602): no token was sent where one is required.</summary>
    NotAuthorized,

    /// <summary>
    /// <c>Session terminated, re-login, please.</c> (-32602): the session or token is unknown or expired (on 7.0.31 and
    /// the 8.0 release candidate: an unknown bearer token answers this too). Refused before the method ran, so a session credential may log in
    /// again and re-send.
    /// </summary>
    SessionTerminated,

    /// <summary><c>API token expired.</c> (-32500): the API token's own expiry passed; not recoverable by the client.</summary>
    TokenExpired,

    /// <summary>
    /// <c>user.login</c> failed: <c>Incorrect user name or password or account is temporarily blocked.</c> and
    /// <c>Authentication failed:</c> (-32500), or <c>No permissions for system access.</c> (-32602, <c>ZBX_API_ERROR_PARAMETERS</c>:
    /// a disabled user group or role, <c>CUser.php</c> 7.0:2734, 2742). Retrying at once only extends a block.
    /// </summary>
    LoginFailed,

    /// <summary>The role forbids it (-32500): <c>No permissions to call "</c> or <c>You do not have permission to perform this operation.</c>.</summary>
    Permission,

    /// <summary>
    /// <c>No permissions to referred object or it does not exist!</c>: Zabbix does not say which. For a Super admin it
    /// always means the object does not exist; otherwise a follow-up <c>get</c> tells.
    /// </summary>
    PermissionOrNotFound,

    /// <summary>The method does not exist on this server version (-32601, <c>Incorrect API "</c> / <c>Incorrect method "</c>).</summary>
    MethodNotFound,

    /// <summary>
    /// <c>The "x.y" method must be called without authorization header.</c> (-32602): a defect in the caller, which sent a
    /// token to a method that refuses one (<c>CLocalApiClient.php</c> 7.0:84-97, <c>CJsonRpc.php</c> 8.0:116-124).
    /// </summary>
    AuthorizationHeaderRefused,

    /// <summary>
    /// The frontend could not serve the call at all: -32603 (<c>api_jsonrpc.php</c> answers any uncaught throwable, a
    /// database that cannot be reached at start-up included, with HTTP 200 and -32603). Worth another try.
    /// </summary>
    Transient,

    /// <summary>
    /// <c>Database error occurred.</c> (-32500, <c>ZBX_API_ERROR_DB</c>; <c>System error occurred.</c> to a user who is not
    /// a Super admin, <c>CJsonRpc.php</c> 7.0:147-149): an SQL statement of the method failed and its transaction was rolled
    /// back (<c>CLocalApiClient.php</c> 7.0:129-160, <c>DBend</c> with a failed statement). Not transient: the usual cause
    /// is input the validators let through and the SQL refused (on 7.0.31 and the 8.0 release candidate, <c>correlation.get</c> with
    /// <c>correlationids: [null]</c>), which fails again however often it is sent. The server's log says which statement.
    /// </summary>
    Database,
}
