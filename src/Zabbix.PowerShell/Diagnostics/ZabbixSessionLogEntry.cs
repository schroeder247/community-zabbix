// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell.Diagnostics;

/// <summary>One line a session's own loop kept in its ring buffer, for a <c>Get-Zbx&lt;Session&gt;Log</c> cmdlet.</summary>
/// <param name="Timestamp">When it was logged, on the session's own clock.</param>
/// <param name="Level">The level.</param>
/// <param name="Category">The logger's category.</param>
/// <param name="Message">The formatted message.</param>
public sealed record ZabbixSessionLogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message);
