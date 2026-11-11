// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>The <c>user.login</c> parameters, asking for the user record (<c>userData</c>). Its text form never carries the password.</summary>
/// <param name="Username">The user.</param>
/// <param name="Password">The password.</param>
internal sealed record ZabbixLoginParameters(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password"), ZabbixSecret] string Password)
{
    /// <summary>Ask for the user record rather than the bare session id, so the login can see <c>mfaid</c>.</summary>
    [JsonPropertyName("userData")]
    public bool UserData { get; init; } = true;

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{nameof(ZabbixLoginParameters)} {{ Username = {Username} }}");
}
