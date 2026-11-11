// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using Community.Zabbix.Tests.Conformance;

namespace Community.Zabbix.Tests;

/// <summary>
/// The exception family (docs/design/decisions.md, "Refusals and failures are two families"): a peer's refusal derives
/// from <see cref="ZabbixRefusedException"/>, a wire failure from an <see cref="IOException"/> (the incomplete send among
/// them; the wire's own base for what it knows more about is internal, since nothing public derives from it), anything
/// else from the nearest standard type, never <see cref="Exception"/> itself. Every concrete public exception is sealed, has the three standard constructors and
/// names its own <c>error.type</c>.
/// </summary>
public sealed partial class DesignConformanceTests
{
    private static readonly Type[] TooGeneralBases = [typeof(Exception), typeof(SystemException), typeof(ApplicationException)];

    [Fact]
    public void Exceptions_are_designed_types()
        => AssertNone(Waivers.Outstanding("Exceptions_are_designed_types", UndesignedExceptions(Libraries.PublicTypes)));

    [Fact]
    public void The_exception_rule_finds_an_open_a_generic_and_an_untyped_exception()
    {
        List<string> found = [.. UndesignedExceptions([typeof(OpenProbeException), typeof(GeneralProbeException)])];

        Assert.Contains(found, offender => offender.StartsWith(typeof(OpenProbeException).FullName + ": not sealed", StringComparison.Ordinal));
        Assert.Contains(found, offender => offender.StartsWith(typeof(OpenProbeException).FullName + ": no (string, Exception) constructor", StringComparison.Ordinal));
        Assert.Contains(found, offender => offender.StartsWith(typeof(GeneralProbeException).FullName + ": derives from System.Exception", StringComparison.Ordinal));
        Assert.Contains(found, offender => offender.StartsWith(typeof(GeneralProbeException).FullName + ": names no error.type", StringComparison.Ordinal));
        Assert.Empty(UndesignedExceptions([typeof(ZabbixApiException), typeof(ZabbixServerRefusedException), typeof(ZabbixRefusedException)]));
    }

    [Fact]
    public void Every_refusal_answered_by_a_peer_is_one_family_and_every_wire_failure_an_io_exception()
    {
        Assert.All([typeof(ZabbixApiException), typeof(ZabbixTrapperRefusedException), typeof(ZabbixServerRefusedException), typeof(ZabbixAgentRefusedException), typeof(Zabbix.Proxy.ZabbixProxyRefusedException)],
            refusal => Assert.True(refusal.IsSubclassOf(typeof(ZabbixRefusedException)), refusal.Name));
        Assert.True(typeof(ZabbixWireException).IsSubclassOf(typeof(IOException)));
        Assert.False(typeof(ZabbixWireException).IsPublic);
        Assert.True(typeof(ZabbixSenderIncompleteException).IsSubclassOf(typeof(IOException)));
        Assert.True(typeof(ZabbixItemNotSupportedException).IsSubclassOf(typeof(ZabbixRefusedException)));
        Assert.False(typeof(ZabbixRefusedException).IsSubclassOf(typeof(IOException)));
    }

    internal static IEnumerable<string> UndesignedExceptions(IEnumerable<Type> types)
    {
        foreach (Type type in types.Where(type => typeof(Exception).IsAssignableFrom(type)))
        {
            string name = type.FullName!;
            if (TooGeneralBases.Contains(type.BaseType)
                && type != typeof(ZabbixRefusedException))
                yield return $"{name}: derives from {type.BaseType!.FullName}; derive from the nearest standard type, ZabbixRefusedException or an IOException";
            if (!type.GetInterfaces().Any(contract => contract.FullName == "Community.Zabbix.Diagnostics.IZabbixErrorTyped"))
                yield return $"{name}: names no error.type (IZabbixErrorTyped)";
            if (type.IsAbstract) continue;
            if (!type.IsSealed) yield return $"{name}: not sealed";
            foreach ((Type[] parameters, string shape) in new[] { (Type.EmptyTypes, "()"), ([typeof(string)], "(string)"), ([typeof(string), typeof(Exception)], "(string, Exception)") })
            {
                if (type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, parameters) is null)
                    yield return $"{name}: no {shape} constructor";
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "A probe that breaks the rule.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1852:Seal internal types", Justification = "A probe that breaks the rule.")]
    internal class OpenProbeException : InvalidOperationException
    {
    }

    internal sealed class GeneralProbeException : Exception
    {
        public GeneralProbeException()
        {
        }

        public GeneralProbeException(string message) : base(message)
        {
        }

        public GeneralProbeException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
