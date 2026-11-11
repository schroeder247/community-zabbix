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
/// <para>
/// The name tells an operator which check a line is about; the parameters are where a credential, a connection string or
/// a person's name can sit. <see cref="ZabbixRedactionBuilderExtensions.SetZabbixRedactors"/> sets this redactor for
/// the class, in every package's application:
/// </para>
/// <code language="csharp">
/// builder.Logging.EnableRedaction();
/// builder.Services.AddRedaction(redaction => redaction.SetZabbixRedactors());
/// </code>
/// <para>
/// The logger hands a redactor the value and, by default, a discriminator: the tag's name after a colon
/// (<c>system.uptime:key</c>, <c>LoggerRedactionOptions.ApplyDiscriminator</c>). A key name can't hold a colon, so a
/// name followed by a colon and a tag's name is the bare key, and keeps its name either way. An application that logs to
/// a trusted sink can set a pass-through redactor for the class instead.
/// </para>
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

    /// <summary>
    /// Whether the text is a key name, alone, followed by <c>[</c>, or followed by the logger's discriminator (a colon and
    /// a tag's name); the name's length; and whether parameters follow.
    /// </summary>
    private static (bool IsKey, int Name, bool HasParameters) Shape(ReadOnlySpan<char> text)
    {
        int name = 0;
        while (name < text.Length && ZabbixItemKey.IsNameChar(text[name])) name++;
        if (name == 0) return (false, 0, false);
        if (name == text.Length || IsDiscriminator(text[name..])) return (true, name, false);
        return text[name] == '[' ? (true, name, true) : (false, 0, false);
    }

    /// <summary>
    /// Whether the text is the discriminator the logger appends to a redacted value: a colon and the tag's name, the name
    /// of a log template's placeholder (<c>JustInTimeRedactor</c> in <c>Microsoft.Extensions.Telemetry</c>).
    /// </summary>
    /// <param name="text">The text after a value.</param>
    /// <returns><see langword="true"/> when the text is a colon followed by a tag's name; otherwise <see langword="false"/>.</returns>
    internal static bool IsDiscriminator(ReadOnlySpan<char> text)
    {
        if (text.Length < 2 || text[0] != ':') return false;
        foreach (char c in text[1..])
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.')) return false;
        }
        return true;
    }
}
