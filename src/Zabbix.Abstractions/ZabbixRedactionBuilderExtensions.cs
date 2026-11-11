// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Compliance.Redaction;

namespace Community.Zabbix;

/// <summary>
/// Provides the SDK's redactors for an application's redaction builder: one per class of the SDK's data taxonomy
/// (<see cref="ZabbixDataClassifications"/>). The library itself never collects or exports: it classifies what it logs
/// and leaves the policy to the application.
/// </summary>
public static class ZabbixRedactionBuilderExtensions
{
    /// <summary>
    /// Sets the SDK's redactor for each of its own data classifications: <see cref="ZabbixDataClassifications.Secret"/> is
    /// erased, an item key (<see cref="ZabbixDataClassifications.ItemKey"/>) keeps its name and masks its parameters
    /// (<see cref="ZabbixItemKeyRedactor"/>), and a peer's free text (<see cref="ZabbixDataClassifications.PeerText"/>)
    /// becomes <c>[redacted]</c> (<see cref="ZabbixPeerTextRedactor"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The call changes nothing outside the SDK's taxonomy. Turning redaction on for logging (<c>EnableRedaction</c>), the
    /// fallback redactor for classes without one, and <see cref="ZabbixDataClassifications.Personal"/> stay the
    /// application's: unregistered, a class is erased by the fallback, <c>ErasingRedactor</c> unless the application sets
    /// another.
    /// </para>
    /// <para>
    /// A redactor the application sets for one of these classes after this call replaces the SDK's, such as a
    /// pass-through for <see cref="ZabbixDataClassifications.PeerText"/> on a trusted sink. Keep
    /// <see cref="ZabbixDataClassifications.Secret"/> erased.
    /// </para>
    /// </remarks>
    /// <param name="builder">The application's redaction builder.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp">
    /// builder.Logging.EnableRedaction();
    /// builder.Services.AddRedaction(redaction => redaction.SetZabbixRedactors());
    /// </code>
    /// </example>
    public static IRedactionBuilder SetZabbixRedactors(this IRedactionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .SetRedactor<ZabbixSecretRedactor>(ZabbixDataClassifications.Secret)
            .SetRedactor<ZabbixItemKeyRedactor>(ZabbixDataClassifications.ItemKey)
            .SetRedactor<ZabbixPeerTextRedactor>(ZabbixDataClassifications.PeerText);
    }
}

/// <summary>
/// Erases a credential (<see cref="ZabbixDataClassifications.Secret"/>) to nothing, in every environment: the SDK's own
/// redactor for the class, so the rule doesn't depend on the application's fallback.
/// </summary>
internal sealed class ZabbixSecretRedactor : Redactor
{
    /// <inheritdoc/>
    public override int GetRedactedLength(ReadOnlySpan<char> input) => 0;

    /// <inheritdoc/>
    public override int Redact(ReadOnlySpan<char> source, Span<char> destination) => 0;
}
