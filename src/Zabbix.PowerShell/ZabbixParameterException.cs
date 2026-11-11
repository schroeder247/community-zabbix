// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Represents a command argument that cannot be used, with an error id a script can switch on: a request field's reference
/// that matches nothing (<c>ZabbixReferenceNotFound</c>) or several objects (<c>ZabbixReferenceAmbiguous</c>), a create
/// request missing a field the server requires (<c>ZabbixRequiredFieldMissing</c>). An <see cref="ArgumentException"/>
/// (so <c>catch [ArgumentException]</c> still takes it) whose <see cref="IZabbixErrorTyped.ErrorType"/> is that id, so
/// <see cref="ZabbixErrors.From"/> names it and not the CLR type.
/// </summary>
public sealed class ZabbixParameterException : ArgumentException, IZabbixErrorTyped
{
    /// <summary>An argument that cannot be used, with no detail (id <c>ZabbixParameterInvalid</c>).</summary>
    public ZabbixParameterException()
    {
    }

    /// <summary>An argument that cannot be used (id <c>ZabbixParameterInvalid</c>).</summary>
    /// <param name="message">What is wrong.</param>
    public ZabbixParameterException(string message) : base(message)
    {
    }

    /// <summary>An argument that cannot be used, because of another failure (id <c>ZabbixParameterInvalid</c>).</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">The cause.</param>
    public ZabbixParameterException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>An argument that cannot be used.</summary>
    /// <param name="errorId">The stable error id.</param>
    /// <param name="parameter">The parameter (without the dash).</param>
    /// <param name="message">What is wrong, and what to do.</param>
    public ZabbixParameterException(string errorId, string parameter, string message)
        : base(message, parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorId);
        ErrorId = errorId;
    }

    /// <summary>The stable error id (<c>ZabbixReferenceNotFound</c>, <c>ZabbixRequiredFieldMissing</c>, …).</summary>
    public string ErrorId { get; } = "ZabbixParameterInvalid";

    /// <inheritdoc/>
    string IZabbixErrorTyped.ErrorType => ErrorId;
}
