// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix;

/// <summary>
/// The exception that is thrown when the server's Zabbix version does not have the method, or refuses a field of its
/// request. Thrown before anything is sent; <see cref="ZabbixApiMethods.Supports"/> is the
/// Tester-Doer that avoids it.
/// </summary>
public sealed class ZabbixVersionException : NotSupportedException, IZabbixErrorTyped
{
    /// <summary>A refusal with no detail.</summary>
    public ZabbixVersionException()
    {
    }

    /// <summary>A refusal with a message.</summary>
    /// <param name="message">The message.</param>
    public ZabbixVersionException(string message) : base(message)
    {
    }

    /// <summary>A refusal with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ZabbixVersionException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>The method refused for <paramref name="serverVersion"/>.</summary>
    /// <param name="method">The API method.</param>
    /// <param name="serverVersion">The server's version.</param>
    /// <param name="capability">What the table says about the method.</param>
    internal ZabbixVersionException(ZabbixApiMethod method, Version serverVersion, ZabbixApiMethodInfo capability)
        : base(Describe(method, serverVersion, capability))
    {
        Method = method;
        ServerVersion = serverVersion;
        Since = capability.Since;
        RemovedIn = capability.RemovedIn;
        Replacement = capability.Replacement;
    }

    /// <summary>A server too old for what was asked of it, with the reason in <paramref name="message"/>.</summary>
    /// <param name="message">The reason, a sentence.</param>
    /// <param name="serverVersion">The server's version.</param>
    internal ZabbixVersionException(string message, Version serverVersion)
        : base(message)
    {
        ServerVersion = serverVersion;
    }

    /// <summary>A request field refused for <paramref name="serverVersion"/>.</summary>
    /// <param name="serverVersion">The server's version.</param>
    /// <param name="field">What the field table says about the field.</param>
    internal ZabbixVersionException(Version serverVersion, ZabbixApiField field)
        : base($"Zabbix {serverVersion} does not accept {field.Field} in {field.Method}: it was removed in {field.RemovedIn.ToString(2)}"
               + (field.Replacement is { } use ? $"; use {use}." : "."))
    {
        Method = field.Method;
        Field = field.Field;
        ServerVersion = serverVersion;
        RemovedIn = field.RemovedIn;
        Replacement = field.Replacement;
    }

    /// <summary>The API method, e.g. <see cref="ZabbixApiMethod.HostMassUpdate"/>; <see langword="null"/> when the refusal names none.</summary>
    public ZabbixApiMethod? Method { get; }

    /// <summary>The request field the server refuses, e.g. <c>hostids</c>; null when the whole method is missing.</summary>
    public string? Field { get; }

    /// <summary>What to use instead, when the source names it: a field (<c>hosts</c>) for a refused field, a sentence for a removed method.</summary>
    public string? Replacement { get; }

    /// <summary>The server's version.</summary>
    public Version? ServerVersion { get; }

    /// <summary>The first version with the method.</summary>
    public Version? Since { get; }

    /// <summary>The first version without it, if it was removed.</summary>
    public Version? RemovedIn { get; }

    private static string Describe(ZabbixApiMethod method, Version server, ZabbixApiMethodInfo capability)
        => capability.RemovedIn is { } removed && ZabbixApiMethods.AtLeast(server, removed)
            ? $"Zabbix {server} does not have {method}: it was removed in {removed.ToString(2)}" + (capability.Replacement is { } use ? $"; {use}." : ".")
            : $"Zabbix {server} does not have {method}: it arrived in {capability.Since.ToString(2)}.";

    /// <summary>The OpenTelemetry <c>error.type</c>: the call was not made because the server's version does not have it.</summary>
    string IZabbixErrorTyped.ErrorType => "version_unsupported";
}
