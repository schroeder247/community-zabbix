// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Names a wire field the way the records name their members: the member name the description gives it on any record
/// (the generated <c>ZabbixWireNames.g.cs</c>, from Zabbix.Generator's ZabbixCmdletEmitter), else its words in
/// PascalCase (<c>vendor_name</c> is <c>VendorName</c>). A read record's unmodelled field is shown by that name beside the
/// modelled members, never in the wire's casing.
/// </summary>
internal static partial class ZabbixWireNames
{
    /// <summary>Every modelled wire name and its member name; implemented in the generated <c>ZabbixWireNames.g.cs</c>.</summary>
    private static partial Dictionary<string, string> Build();

    private static readonly FrozenDictionary<string, string> s_members = Build().ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The member name of the wire field <paramref name="wire"/>.</summary>
    /// <param name="wire">The wire name (<c>vendor_name</c>).</param>
    public static string MemberOf(string wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (s_members.TryGetValue(wire, out string? member)) return member;
        var name = new StringBuilder(wire.Length);
        foreach (string word in wire.Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries))
            name.Append(char.ToUpper(word[0], CultureInfo.InvariantCulture)).Append(word, 1, word.Length - 1);
        return name.Length == 0 ? wire : name.ToString();
    }
}
