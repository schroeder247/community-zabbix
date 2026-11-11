// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell;

/// <summary>An object for the output stream.</summary>
internal sealed record OutputWrite(object? Value) : PipelineWrite;
