// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>The two fields of <c>user.login</c>'s user record the credential reads; the rest are ignored, never kept.</summary>
/// <param name="SessionId">The session id: the token from then on.</param>
/// <param name="MfaId">The MFA method the user must complete; <c>0</c> for none. A JSON number on 7.0 and 8.0; text is read too.</param>
internal sealed record ZabbixLoginUser(
    [property: JsonPropertyName("sessionid"), ZabbixSecret] string? SessionId,
    [property: JsonPropertyName("mfaid"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long? MfaId)
{
    /// <inheritdoc/>
    public override string ToString() => nameof(ZabbixLoginUser);                    // a session id is a secret
}
