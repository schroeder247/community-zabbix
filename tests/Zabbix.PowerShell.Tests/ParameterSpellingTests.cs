// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
extern alias ZabbixGenerator;

using System.Management.Automation;
using System.Reflection;
using ZabbixGenerator::Community.Zabbix.Generator;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// A parameter is spelled as the record member it sets (ADR 0013, section 7): no parameter or alias of the three modules
/// keeps a spelling <c>schema/words.json</c>'s <c>respell</c> section retires, generated (<c>-UserName</c>, from a
/// respelled field) or written by hand (<c>-SnmpAuthenticationPassphrase</c>). The commands keep their nouns.
/// </summary>
public sealed class ParameterSpellingTests
{
    [Fact]
    public void No_parameter_or_alias_keeps_a_spelling_the_rename_map_retires()
    {
        string[] retired = [.. ModuleUnderTest.AllCmdlets
            .SelectMany(type => type.GetProperties().Where(property => property.GetCustomAttributes<ParameterAttribute>(true).Any())
                .SelectMany(property => property.GetCustomAttributes<AliasAttribute>(true).SelectMany(alias => alias.AliasNames).Prepend(property.Name))
                .Where(name => ZabbixRespellings.Name(name) != name)
                .Select(name => $"{type.GetCustomAttribute<CmdletAttribute>()?.VerbName}-{type.GetCustomAttribute<CmdletAttribute>()?.NounName} -{name}: -{ZabbixRespellings.Name(name)}"))
            .Distinct()
            .Order(StringComparer.Ordinal)];

        Assert.Empty(retired);
    }
}
