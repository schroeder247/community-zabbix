// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// The service key a named agent's items are registered under: its <see cref="ZabbixItemProvider"/>s and its <c>AddItem</c>
/// sets. An agent that answers keys composes its table from the services of its own name's key.
/// </summary>
/// <param name="Name">The agent's name.</param>
internal sealed record ZabbixItemsKey(string Name);
