// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Compliance.Redaction;

namespace Community.Zabbix;

/// <summary>
/// Redacts an item key (<see cref="ZabbixDataClassifications.ItemKey"/>) to its name, masking its parameters:
/// <c>web.page.get[host,,user:password]</c> becomes <c>web.page.get[***]</c>, and a key without parameters keeps its
/// name. A text that does not start with a key name followed by <c>[</c> or its end is erased, because it is not a key
/// whose name is known to carry nothing.
/// </summary>
/// <remarks>
/// The name tells an operator which check a line is about; the parameters are where a credential, a connection string or
/// a person's name can sit. <c>AddZabbixRedaction</c> registers this redactor for the class. An application that takes
/// the wire or the hosts without <c>Zabbix.Sdk</c> registers it itself, beside the erasing rule for
/// <see cref="ZabbixDataClassifications.Secret"/>:
/// <code>
/// builder.Logging.EnableRedaction(options => options.ApplyDiscriminator = false);   // a bare key keeps its name
/// builder.Services.AddRedaction(redaction => redaction
///     .SetRedactor&lt;ErasingRedactor&gt;(ZabbixDataClassifications.Secret)
///     .SetRedactor&lt;ZabbixItemKeyRedactor&gt;(ZabbixDataClassifications.ItemKey)
///     .SetRedactor&lt;ZabbixPeerTextRedactor&gt;(ZabbixDataClassifications.PeerText));
/// </code>
/// An application that logs to a trusted sink can register a pass-through for the class instead.
/// </remarks>
public sealed class ZabbixItemKeyRedactor : Redactor
{
    private const string Mask = "[***]";

    /// <inheritdoc/>
    public override int GetRedactedLength(ReadOnlySpan<char> input) => Shape(input) switch
    {
        (true, int name, true) => name + Mask.Length,
        (true, int name, false) => name,
        _ => 0,
    };

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="GetRedactedLength"/>.</exception>
    public override int Redact(ReadOnlySpan<char> source, Span<char> destination)
    {
        int length = GetRedactedLength(source);
        if (destination.Length < length)
            throw new ArgumentException($"The destination holds {destination.Length} character(s) and the redacted key needs {length}.", nameof(destination));
        (bool isKey, int name, bool hasParameters) = Shape(source);
        if (!isKey) return 0;
        source[..name].CopyTo(destination);
        if (hasParameters) Mask.AsSpan().CopyTo(destination[name..]);
        return length;
    }

    /// <summary>Whether the text is a key name, alone or followed by <c>[</c>; the name's length; and whether parameters follow.</summary>
    private static (bool IsKey, int Name, bool HasParameters) Shape(ReadOnlySpan<char> text)
    {
        int name = 0;
        while (name < text.Length && ZabbixItemKey.IsNameChar(text[name])) name++;
        if (name == 0) return (false, 0, false);
        if (name == text.Length) return (true, name, false);
        return text[name] == '[' ? (true, name, true) : (false, 0, false);
    }
}
