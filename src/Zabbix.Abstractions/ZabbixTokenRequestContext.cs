// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>What a credential is handed when a token is asked for: the transport (for an anonymous <c>user.login</c>), the method, and the clock.</summary>
/// <param name="Transport">The transport the request will go through.</param>
/// <param name="Method">The method being called.</param>
/// <param name="Time">The clock the transport runs on, for a credential that backs off.</param>
public readonly record struct ZabbixTokenRequestContext(IZabbixTransport Transport, string Method, TimeProvider Time);
