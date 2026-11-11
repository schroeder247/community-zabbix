// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>A non-terminating error the operation found (a name that resolves to nothing).</summary>
internal sealed record ErrorWrite(ErrorRecord Record) : PipelineWrite;
