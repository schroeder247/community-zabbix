// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Reflection;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// The rule a generated <c>New-</c> or <c>Set-</c> applies to what a piped record bound by property name. The binder
/// matches a property by its name alone, and the same name means different things on different records: an event's
/// <c>Name</c> is not a host's name, an item's <c>HostId</c> is not the host a new item is created on, a template's
/// <c>Groups</c> are template groups. So a piped Zabbix record (a read record, or any record of the SDK's model) keeps
/// only what is its own: a reference to its own object (<c>-Template</c> from a template) and a parameter that names the
/// record's own id (<c>-HostId</c> from a host, <c>-TemplateId</c> from a template). Every other property it bound is
/// unbound. A record that is not a Zabbix record (an <c>Import-Csv</c> row, a <c>[pscustomobject]</c>) names its
/// columns as the user wrote them, and binds them all.
/// </summary>
internal static class ZabbixPipedRecord
{
    /// <summary>The engine's own id for a piped object that binds no parameter, which a command that takes every piped object writes for it.</summary>
    internal const string EngineInputNotBound = "InputObjectNotBound";

    /// <summary>Whether <paramref name="value"/> is a record of the SDK's model, whose property names are Zabbix's fields.</summary>
    /// <param name="value">The piped object, unwrapped.</param>
    public static bool IsZabbixRecord(object? value)
        => value is not null && (ZabbixEntities.IsEntity(value.GetType()) || value.GetType().Assembly == typeof(ZabbixHost).Assembly);

    /// <summary>
    /// Unbinds every parameter a piped record of <paramref name="record"/> bound by property name that is not its own
    /// (the class's rule): its value is cleared and it leaves <c>MyInvocation.BoundParameters</c>, so a required-field
    /// check sees it as not given.
    /// </summary>
    /// <param name="cmdlet">The command.</param>
    /// <param name="record">The piped record's type.</param>
    /// <param name="written">The parameters written on the command line, which the pipeline never rebinds.</param>
    /// <param name="exempt">A parameter the pipeline binds by value (the command's own <c>-InputObject</c>).</param>
    public static void DropForeign(PSCmdlet cmdlet, Type record, IReadOnlySet<string> written, string exempt)
    {
        string? own = ZabbixEntities.IdMemberOf(record);
        foreach (string key in cmdlet.MyInvocation.BoundParameters.Keys.ToArray())
        {
            if (key == exempt || key == "PipedRecord" || written.Contains(key)) continue;
            PropertyInfo? property = cmdlet.GetType().GetProperty(key);
            if (property is null || Owns(property, record, own)) continue;
            property.SetValue(cmdlet, property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null ? Activator.CreateInstance(property.PropertyType) : null);
            cmdlet.MyInvocation.BoundParameters.Remove(key);
        }
    }

    /// <summary>
    /// Whether the piped record bound something besides <paramref name="exempt"/> and what was written: a piped object
    /// that binds nothing is refused as the binder refuses one (<c>InputObjectNotBound</c>).
    /// </summary>
    /// <param name="cmdlet">The command.</param>
    /// <param name="written">The parameters written on the command line.</param>
    /// <param name="exempt">The by-value parameter.</param>
    public static bool BoundAny(PSCmdlet cmdlet, IReadOnlySet<string> written, string exempt)
        => cmdlet.MyInvocation.BoundParameters.Keys.Any(key => key != exempt && key != "PipedRecord" && !written.Contains(key));

    /// <summary>Why a piped object bound nothing, for the refusal's message.</summary>
    /// <param name="piped">The piped object, unwrapped.</param>
    public static string WhyUnbound(object piped) => piped switch
    {
        System.Collections.IDictionary => "a hashtable's keys are not properties: pipe [pscustomobject]$hashtable instead",
        _ when IsZabbixRecord(piped) => $"a {piped.GetType().Name} carries nothing of its own that the command takes",
        _ => "no property of it names a parameter of the command",
    };

    private static bool Owns(PropertyInfo property, Type record, string? own)
    {
        Type type = property.PropertyType.IsArray ? property.PropertyType.GetElementType()! : property.PropertyType;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ZabbixReference<>)) return type.GetGenericArguments()[0] == record;
        if (own is null) return false;
        return string.Equals(property.Name, own, StringComparison.OrdinalIgnoreCase)
            || property.GetCustomAttributes<AliasAttribute>(inherit: true).SelectMany(alias => alias.AliasNames).Contains(own, StringComparer.OrdinalIgnoreCase);
    }
}
