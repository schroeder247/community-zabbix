// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix.PowerShell.Sessions;

/// <summary>
/// Represents a scriptblock run through a <see cref="ZabbixScriptRunspace"/> that wrote to its error stream: a
/// <see cref="RuntimeException"/>, the type PowerShell raises for a failing script, carrying the script's own error
/// record. Its error id is <c>ZabbixScriptFailed</c>.
/// </summary>
public sealed class ZabbixScriptRunspaceException : RuntimeException, IZabbixErrorTyped
{
    /// <summary>Creates the exception with no message.</summary>
    public ZabbixScriptRunspaceException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The error's message.</param>
    public ZabbixScriptRunspaceException(string message) : base(message)
    {
    }

    /// <summary>A scriptblock's error stream carried a failure.</summary>
    /// <param name="message">The error's message.</param>
    /// <param name="innerException">The underlying exception, when the error record carried one.</param>
    public ZabbixScriptRunspaceException(string message, Exception? innerException) : base(message, innerException)
    {
    }

    /// <summary>A scriptblock's error stream carried <paramref name="errorRecord"/>.</summary>
    /// <param name="message">The error's message.</param>
    /// <param name="innerException">The underlying exception, when the error record carried one.</param>
    /// <param name="errorRecord">The error record the script wrote.</param>
    public ZabbixScriptRunspaceException(string message, Exception? innerException, ErrorRecord errorRecord) : base(message, innerException, errorRecord)
    {
    }

    /// <inheritdoc/>
    string IZabbixErrorTyped.ErrorType => "ZabbixScriptFailed";
}
