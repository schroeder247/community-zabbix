// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Community.Zabbix;

/// <summary>
/// Represents where an agent sends, as Zabbix's <c>ServerActive</c> spells it: <c>a;b,c</c>. A <c>,</c> separates
/// independent destinations — each gets every value, over its own connection, with its own buffer and delivery accounting (upstream
/// starts one active-checks thread per destination) — and a <c>;</c> separates the HA nodes of one destination, tried in
/// order with failover (<c>zbx_set_data_destination_hosts</c>, <c>src/libs/zbxcfg/cfg.c</c> 853-908, 7.0/7.4/8.0 alike).
/// Each element is a <see cref="ZabbixServerNode"/> (<c>zbx_parse_serveractive_element</c>), and an address named twice
/// anywhere in the value is refused ("specified more than once"). The type is the list of destinations, each an ordered
/// node list — exactly what <c>ZabbixFrames.Over</c> takes — so a destination composes into an exchange as it is.
/// </summary>
/// <remarks>
/// Owned because it is Zabbix's grammar, not a general one: no BCL parser splits a two-level list, and the
/// element grammar is <see cref="ZabbixServerNode"/>'s. Configuration binds it as text (the source-generated binder does not
/// use type converters), and the options' validators parse it with
/// <see cref="TryParse(string?, IFormatProvider?, out ZabbixServerActive)"/>.
/// </remarks>
internal sealed class ZabbixServerActive : IReadOnlyList<IReadOnlyList<ZabbixServerNode>>, ISpanParsable<ZabbixServerActive>, ISpanFormattable, IEquatable<ZabbixServerActive>
{
    private readonly IReadOnlyList<ZabbixServerNode>[] _destinations;

    private ZabbixServerActive(IReadOnlyList<ZabbixServerNode>[] destinations) => _destinations = destinations;

    /// <summary>No destination: an agent with nowhere to send (upstream: no active checks).</summary>
    public static ZabbixServerActive Empty { get; } = new([]);

    /// <summary>One destination of one or more HA nodes (<c>a;b</c>).</summary>
    /// <param name="nodes">The nodes, in order of preference.</param>
    /// <exception cref="ArgumentException">No node, a <see langword="default"/> node, or a node named twice.</exception>
    /// <returns>The destinations, each with its HA nodes, as the agent reads them.</returns>
    public static ZabbixServerActive Of(params IEnumerable<ZabbixServerNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return Empty.WithDestination(nodes);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ZabbixServerNode> this[int index] => _destinations[index];

    /// <inheritdoc/>
    public int Count => _destinations.Length;

    /// <summary>Every node of every destination, in order.</summary>
    public IEnumerable<ZabbixServerNode> Nodes => _destinations.SelectMany(destination => destination);

    /// <summary>A new value with <paramref name="nodes"/> as one more independent destination (<c>,</c>).</summary>
    /// <param name="nodes">The destination's HA nodes, in order.</param>
    /// <exception cref="ArgumentException">No node, a <see langword="default"/> node, or a node already named.</exception>
    /// <returns>A list with the destination added after the others.</returns>
    public ZabbixServerActive WithDestination(IEnumerable<ZabbixServerNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ZabbixServerNode[] cluster = [.. nodes];
        if (cluster.Length == 0) throw new ArgumentException("A destination needs at least one node.", nameof(nodes));
        Check(cluster, nameof(nodes));
        return new ZabbixServerActive([.. _destinations, cluster]);
    }

    /// <summary>
    /// A new value with <paramref name="node"/> as one more HA node (<c>;</c>) of the last destination, or as the first
    /// destination when there is none.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <exception cref="ArgumentException">A <see langword="default"/> node, or a node already named.</exception>
    /// <returns>A list whose last destination has the node added to its HA nodes.</returns>
    public ZabbixServerActive WithFailover(ZabbixServerNode node)
    {
        if (_destinations.Length == 0) return WithDestination([node]);
        Check([node], nameof(node));
        IReadOnlyList<ZabbixServerNode>[] destinations = [.. _destinations];
        destinations[^1] = [.. destinations[^1], node];
        return new ZabbixServerActive(destinations);
    }

    private void Check(ZabbixServerNode[] added, string parameter)
    {
        if (added.Any(node => node.Port == 0)) throw new ArgumentException("The destination holds an empty (default) node.", parameter);
        var seen = new HashSet<ZabbixServerNode>(Nodes);
        foreach (ZabbixServerNode node in added)
            if (!seen.Add(node)) throw new ArgumentException($"The address \"{node}\" is specified more than once.", parameter);
    }

    /// <inheritdoc/>
    public static ZabbixServerActive Parse(string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan(), provider);
    }

    /// <inheritdoc/>
    public static ZabbixServerActive Parse(ReadOnlySpan<char> s, IFormatProvider? provider)
        => TryParse(s, provider, out ZabbixServerActive? value, out string? error) ? value : throw new FormatException(error);

    /// <inheritdoc/>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out ZabbixServerActive result)
        => TryParse(s.AsSpan(), provider, out result, out _);

    /// <inheritdoc/>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, [MaybeNullWhen(false)] out ZabbixServerActive result)
        => TryParse(s, provider, out result, out _);

    /// <summary>Parses <c>ServerActive</c>, and says why it does not parse in upstream's words.</summary>
    /// <param name="s">The value, e.g. <c>zbx1;zbx2:10052,proxy-b</c>.</param>
    /// <param name="provider">Unused: the grammar is culture-free.</param>
    /// <param name="result">The destinations.</param>
    /// <param name="error">Why not, when it does not parse.</param>
    /// <returns>Whether it parses.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, [MaybeNullWhen(false)] out ZabbixServerActive result, [NotNullWhen(false)] out string? error)
    {
        result = null;
        var destinations = new List<IReadOnlyList<ZabbixServerNode>>();
        var seen = new HashSet<ZabbixServerNode>();
        foreach (Range destination in s.Split(','))
        {
            var cluster = new List<ZabbixServerNode>();
            ReadOnlySpan<char> clusterText = s[destination];
            foreach (Range element in clusterText.Split(';'))
            {
                ReadOnlySpan<char> text = clusterText[element];
                if (!ZabbixServerNode.TryParse(text, provider, out ZabbixServerNode node))
                {
                    error = $"error parsing the \"ServerActive\" parameter: address \"{text}\" is invalid";
                    return false;
                }
                if (!seen.Add(node))
                {
                    error = $"error parsing the \"ServerActive\" parameter: address \"{text}\" specified more than once";
                    return false;
                }
                cluster.Add(node);
            }
            destinations.Add([.. cluster]);
        }
        result = new ZabbixServerActive([.. destinations]);
        error = null;
        return true;
    }

    /// <summary>The value as Zabbix writes it: destinations joined by <c>,</c>, their nodes by <c>;</c>.</summary>
    /// <returns>The list as the agent's configuration writes it: destinations by <c>,</c>, HA nodes by <c>;</c>.</returns>
    public override string ToString()
    {
        var text = new StringBuilder();
        foreach (IReadOnlyList<ZabbixServerNode> destination in _destinations)
        {
            if (text.Length > 0) text.Append(',');
            text.AppendJoin(';', destination);
        }
        return text.ToString();
    }

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        string text = ToString();
        charsWritten = text.TryCopyTo(destination) ? text.Length : 0;
        return charsWritten == text.Length;
    }

    /// <inheritdoc/>
    public IEnumerator<IReadOnlyList<ZabbixServerNode>> GetEnumerator() => ((IEnumerable<IReadOnlyList<ZabbixServerNode>>)_destinations).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public bool Equals(ZabbixServerActive? other)
        => other is not null && _destinations.Length == other._destinations.Length
           && _destinations.Zip(other._destinations).All(pair => pair.First.SequenceEqual(pair.Second));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ZabbixServerActive);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (ZabbixServerNode node in Nodes) hash.Add(node);
        return hash.ToHashCode();
    }
}
