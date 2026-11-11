// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>
/// Gives every enum a value of zero, as the Framework Design Guidelines' enum design asks: a simple enum's default is a
/// member, and a flags enum's zero is <c>None</c>, the empty set. Where Zabbix numbers a family from one
/// (<c>USER_TYPE_ZABBIX_USER</c> is 1), the enum gains <c>None = 0</c> for the type's default, a value no Zabbix define has,
/// so no wire value moves. It runs once, in <see cref="ApiDescription.Read(string, List{EmitProblem})"/>, so the records,
/// the cmdlets and the views all see the member.
/// </summary>
internal static class ZabbixEnumDefaults
{
    /// <summary>The name of the zero member an enum gains.</summary>
    public const string None = "None";

    /// <summary><paramref name="api"/> with <c>None = 0</c> first in every enum that has no member of value zero.</summary>
    /// <param name="api">The description, its names respelled.</param>
    public static ApiDescription Apply(ApiDescription api)
    {
        if (api is null) throw new ArgumentNullException(nameof(api));
        return api with { Enums = [.. api.Enums.Select(WithZero)] };
    }

    private static EnumDescription WithZero(EnumDescription e)
    {
        if (e.Members.Any(m => m.Value == 0) || e.Members.Any(m => m.Name == None)) return e;
        string summary = e.Flags
            ? "None of the values: the empty set, which no <c>" + e.Family + "_*</c> define names."
            : "No value: the type's default, which no <c>" + e.Family + "_*</c> define names, so the server refuses it.";
        return e with { Members = [new EnumMember(None, 0, null, summary, null, null), .. e.Members] };
    }
}
