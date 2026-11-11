// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>
/// An object whose requests carry a visible name beside a technical one: the technical name's parameter is
/// <c>-Name</c> (alias <c>-Host</c>), the one <c>Get-</c> selects by, and the visible name's is <c>-DisplayName</c>.
/// </summary>
/// <param name="Display">The wire name of the visible name.</param>
/// <param name="Technical">The wire name of the technical name.</param>
internal sealed record NameFields(string Display, string Technical);
