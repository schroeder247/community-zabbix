// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Runtime.CompilerServices;

namespace Community.Zabbix;

/// <summary>
/// Represents a Zabbix API method: <c>host.get</c>, <c>configuration.import</c>. Its members name every method of every
/// version the SDK describes, <see cref="HostGet"/> and <see cref="ConfigurationImport"/> among them, as
/// <see cref="System.Net.Http.HttpMethod.Get"/> names an HTTP method; a method the SDK doesn't name is
/// <c>new ZabbixApiMethod("object.method")</c>, or <see cref="Parse(string)"/>.
/// </summary>
/// <remarks>
/// Two methods are equal when their names are, ignoring case: Zabbix lower-cases a method before it routes it
/// (<c>CLocalApiClient::callMethod</c>). The default value names no method, and a member that takes a method refuses it.
/// </remarks>
/// <example>
/// <code language="csharp">
/// bool hasRules = ZabbixApiMethods.Supports(ZabbixApiMethod.CepRuleGet, version);
/// builder.WithTimeout(ZabbixApiMethod.ConfigurationImport, TimeSpan.FromMinutes(5));
/// </code>
/// </example>
public readonly partial struct ZabbixApiMethod : IEquatable<ZabbixApiMethod>
{
    private static readonly System.Buffers.SearchValues<char> s_letters = System.Buffers.SearchValues.Create("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    private readonly string? _name;

    /// <summary>Initializes a method from its name.</summary>
    /// <param name="name">The method, as Zabbix routes it: an object and a method, such as <c>host.get</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    /// <exception cref="FormatException"><paramref name="name"/> isn't an object and a method, each of letters and digits, joined by a dot.</exception>
    public ZabbixApiMethod(string name)
        : this(name, check: true)
    {
    }

    // A method the transport carries as text (check: false) is named as its caller sent it: the server judges a name that
    // isn't one.
    private ZabbixApiMethod(string name, bool check)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (check && !IsMethodName(name))
            throw new FormatException($"'{name}' isn't a Zabbix API method: write an object and a method, each of letters and digits, joined by a dot, such as host.get.");
        _name = name;
    }

    /// <summary>Gets the method's name, as Zabbix routes it: <c>host.get</c>; empty for the default value.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>Gets the method <paramref name="name"/> names: a method the SDK describes, spelled as its member is, or another method.</summary>
    /// <param name="name">The method's name, in any case: <c>host.get</c>, <c>HOST.GET</c>.</param>
    /// <returns>The method.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    /// <exception cref="FormatException"><paramref name="name"/> isn't an object and a method joined by a dot.</exception>
    public static ZabbixApiMethod Parse(string name)
    {
        var method = new ZabbixApiMethod(name);
        return ZabbixApiMethods.TryGet(method, out ZabbixApiMethodInfo? info) ? info.Method : method;
    }

    /// <summary>Indicates whether this method is <paramref name="other"/>, their names compared without regard to case.</summary>
    /// <param name="other">The method to compare with this one.</param>
    /// <returns><see langword="true"/> when both name the same method; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixApiMethod other) => string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ZabbixApiMethod other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);

    /// <summary>Returns the method's name.</summary>
    /// <returns>The name, as Zabbix routes it: <c>host.get</c>.</returns>
    public override string ToString() => Name;

    /// <summary>Indicates whether two methods are the same.</summary>
    /// <param name="left">The first method.</param>
    /// <param name="right">The second method.</param>
    /// <returns><see langword="true"/> when both name the same method; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(ZabbixApiMethod left, ZabbixApiMethod right) => left.Equals(right);

    /// <summary>Indicates whether two methods differ.</summary>
    /// <param name="left">The first method.</param>
    /// <param name="right">The second method.</param>
    /// <returns><see langword="true"/> when they name different methods; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(ZabbixApiMethod left, ZabbixApiMethod right) => !left.Equals(right);

    /// <summary>The method a request names, as its text was sent: no check, the server judges it.</summary>
    /// <param name="name">The method's name, as the transport sends it.</param>
    /// <returns>The method.</returns>
    internal static ZabbixApiMethod FromWire(string name) => new(name, check: false);

    /// <summary>Throws when <paramref name="method"/> is the default value, which names no method.</summary>
    /// <param name="method">The caller's method.</param>
    /// <param name="parameter">The caller's parameter.</param>
    /// <exception cref="ArgumentException"><paramref name="method"/> is the default value.</exception>
    internal static void ThrowIfDefault(ZabbixApiMethod method, [CallerArgumentExpression(nameof(method))] string? parameter = null)
    {
        if (method._name is null) throw new ArgumentException("The method is the default value, which names no method: use a member such as ZabbixApiMethod.HostGet, or new ZabbixApiMethod(\"object.method\").", parameter);
    }

    // object.method: letters and digits on each side of one dot (apiinfo.version, user.checkAuthentication).
    private static bool IsMethodName(string name)
    {
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && dot < name.Length - 1 && name.IndexOf('.', dot + 1) < 0
            && !name.AsSpan(0, dot).ContainsAnyExcept(s_letters) && !name.AsSpan(dot + 1).ContainsAnyExcept(s_letters);
    }
}
