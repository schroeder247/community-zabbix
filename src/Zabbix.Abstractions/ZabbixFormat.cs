// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>The format strings the SDK's own formattable values take: the general one only, as Zabbix writes them.</summary>
internal static class ZabbixFormat
{
    /// <summary>Refuses a format other than the general one (none, <c>G</c> or <c>g</c>), as <see cref="IFormattable"/> asks.</summary>
    /// <param name="format">The format string a caller passed.</param>
    /// <param name="type">The value's type, for the message.</param>
    /// <exception cref="FormatException">The format is one the type does not know.</exception>
    public static void ThrowIfNotGeneral(ReadOnlySpan<char> format, string type)
    {
        if (format.IsEmpty || format is "G" or "g") return;
        throw new FormatException($"'{format}' is not a format a {type} knows; it takes only the general format (G).");
    }
}
