// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Items;

/// <summary>
/// A bundled key that cannot be answered, with the reason the server shows, in the SDK's own words ("The key has more
/// parameters than it takes.", "The key's second parameter is not valid."). The item set answers it as not supported,
/// never logs it.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "It never leaves the bundled items: the item set answers it as the key's not-supported reason.")]
internal sealed class ZabbixItemException : InvalidOperationException
{
    private static readonly System.Collections.Immutable.ImmutableArray<string> s_ordinals = ["first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth", "eleventh"];

    public ZabbixItemException()
    {
    }

    public ZabbixItemException(string message)
        : base(message)
    {
    }

    public ZabbixItemException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>"The key has more parameters than it takes."</summary>
    public static ZabbixItemException TooManyParameters() => new("The key has more parameters than it takes.");

    /// <summary>"The key's first parameter is not valid.", "The key's second parameter is not valid.", … for the parameter at <paramref name="index"/>, from zero.</summary>
    public static ZabbixItemException InvalidParameter(int index) => new($"The key's {s_ordinals[index]} parameter is not valid.");

    /// <summary>The parameter at <paramref name="index"/>, from zero, is not a regular expression .NET can read.</summary>
    public static ZabbixItemException InvalidPattern(int index, ArgumentException invalid) => new($"The {s_ordinals[index]} parameter is not a valid regular expression: {invalid.Message}", invalid);

    /// <summary>The key is not answered on the running operating system.</summary>
    public static ZabbixItemException NotOnThisSystem() => new($"Not supported on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}.");
}
