// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell;

/// <summary>A log record for the stream its level maps to.</summary>
internal sealed record LogWrite(LogLevel Level, EventId EventId, string Category, string Message, Exception? Exception) : PipelineWrite;
