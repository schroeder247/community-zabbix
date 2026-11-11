// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>One emitted source file: its hint name and its text.</summary>
/// <param name="Name">The hint name (<c>ZabbixHost.g.cs</c>).</param>
/// <param name="Code">The C# text.</param>
internal sealed record GeneratedSource(string Name, string Code);
