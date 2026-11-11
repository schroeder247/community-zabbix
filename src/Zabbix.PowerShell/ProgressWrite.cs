// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>A progress record for the progress stream (SD04 "Support the WriteProgress Method").</summary>
internal sealed record ProgressWrite(ProgressRecord Record) : PipelineWrite;
