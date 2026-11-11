// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using Community.Zabbix.Tests.Conformance;

namespace Community.Zabbix.Tests;

/// <summary>
/// Every async member can be cancelled (docs/adr/0013-builders-verbs-and-typed-values.md, section 6): a public method that
/// returns a task or an asynchronous stream takes a <see cref="CancellationToken"/> last, optional on every overload, so no
/// overload forwards without one. The rule covers the public methods a caller calls. Exempt, computed: an implementation or
/// override of a contract declared outside the libraries (<c>IAsyncDisposable.DisposeAsync</c>, a hosted service's start
/// and stop), whose shape is that contract's. The libraries suppress RS0026 and RS0027 for the optional token, and the second
/// rule holds what the two analyzers held besides: in an overload set, another optional parameter is on one overload only,
/// the longest.
/// </summary>
public sealed partial class DesignConformanceTests
{
    [Fact]
    public void Every_async_public_member_takes_an_optional_cancellation_token_last()
        => AssertNone(UntokenedAsyncMembers(Libraries.PublicTypes));

    [Fact]
    public void The_token_rule_finds_a_member_whose_token_is_missing_required_or_not_last()
        => Assert.Equal(
            ["TokenProbe.NotLast(System.Threading.CancellationToken, System.Int32)", "TokenProbe.Required(System.Threading.CancellationToken)",
             "TokenProbe.Stream()", "TokenProbe.Untokened()"],
            UntokenedAsyncMembers([typeof(TokenProbe)]));

    [Fact]
    public void An_overload_takes_no_optional_parameter_but_its_token_unless_it_is_the_one_longest()
        => AssertNone(OptionalBesideOverloads(Libraries.PublicTypes));

    [Fact]
    public void The_overload_rule_finds_optional_parameters_beside_another_overload()
        => Assert.Equal(["TokenProbe.Pick(System.Int32)", "TokenProbe.Pick(System.String)", "TokenProbe.Short(System.String, System.Int32)"],
            OptionalBesideOverloads([typeof(TokenProbe)]));

    internal static IEnumerable<string> UntokenedAsyncMembers(IEnumerable<Type> types)
        => types
            .Where(type => !typeof(Delegate).IsAssignableFrom(type))                     // a delegate's Invoke is the delegate's signature
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => !method.IsSpecialName && IsAsync(method.ReturnType))
            .Where(method => !ImplementsForeignContract(method))                           // IAsyncDisposable.DisposeAsync: the BCL's shape
            .Where(method => method.GetParameters() is not [.., { ParameterType: var last, HasDefaultValue: true }] || last != typeof(CancellationToken))
            .Select(Describe)
            .Order(StringComparer.Ordinal);

    internal static IEnumerable<string> OptionalBesideOverloads(IEnumerable<Type> types)
        => types
            .Where(type => !typeof(Delegate).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName)
                .GroupBy(method => method.Name, StringComparer.Ordinal)
                .Where(overloads => overloads.Count() > 1)
                .SelectMany(overloads =>
                {
                    List<MethodInfo> optional = [.. overloads.Where(method => method.GetParameters().Any(parameter => parameter.IsOptional && !IsTrailingToken(parameter)))];
                    int longest = overloads.Max(method => method.GetParameters().Length);
                    return optional.Count > 1 ? optional : optional.Where(method => method.GetParameters().Length < longest);
                }))
            .Select(Describe)
            .Order(StringComparer.Ordinal);

    private static bool IsAsync(Type type)
        => ReturnsTask(type) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));

    private static bool IsTrailingToken(ParameterInfo parameter)
        => parameter.ParameterType == typeof(CancellationToken) && parameter.Position == ((MethodBase)parameter.Member).GetParameters().Length - 1;
}

/// <summary>Members the token rules must find, beside the ones they must pass.</summary>
internal sealed class TokenProbe
{
    public static Task Untokened() => Task.CompletedTask;

    public static Task Required(CancellationToken cancellationToken) => Task.CompletedTask;

#pragma warning disable CA1068 // the probe IS the violation
    public static Task NotLast(CancellationToken cancellationToken = default, int count = 0) => Task.FromResult(count);
#pragma warning restore CA1068

    public static IAsyncEnumerable<int> Stream() => AsyncEnumerable.Empty<int>();

    public static Task Optional(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public static Task Send(string text, CancellationToken cancellationToken = default) => Task.FromResult(text);

    public static Task Send(Action<string> build, CancellationToken cancellationToken = default) => Task.FromResult(build);

    public static void Pick(int number = 0) => _ = number;

    public static void Pick(string text = "") => _ = text;

    public static void Short(string text, int count = 0) => _ = (text, count);

    public static void Short(string text, int count, string more) => _ = (text, count, more);
}
