// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix;

/// <summary>
/// Provides the application's redaction policy for the SDK's data classification taxonomy, for the app's one <c>Add{App}Telemetry</c> to call. The library itself never collects or exports: it classifies
/// (<see cref="ZabbixDataClassifications"/>) and publishes under <see cref="Diagnostics.ZabbixTelemetryNames.Subscription"/>.
/// </summary>
public static class ZabbixRedactionExtensions
{
    /// <summary>
    /// Enables redaction in the logging pipeline and registers the fail-closed policy:
    /// <see cref="ZabbixDataClassifications.Secret"/> is erased in every environment, an item key
    /// (<see cref="ZabbixDataClassifications.ItemKey"/>) keeps its name and masks its parameters
    /// (<see cref="ZabbixItemKeyRedactor"/>), a peer's free text (<see cref="ZabbixDataClassifications.PeerText"/>)
    /// becomes <c>[redacted]</c> (<see cref="ZabbixPeerTextRedactor"/>), and any class without a redactor (<see cref="ZabbixDataClassifications.Personal"/> until the
    /// app registers an HMAC redactor for it, or another library's class) is erased by the fallback. An app that wants a
    /// different policy for a class registers it after this call; the Secret rule should stay.
    /// </summary>
    /// <remarks>
    /// The policy turns the logger's discriminator off (<see cref="LoggerRedactionOptions.ApplyDiscriminator"/>, on by
    /// default): it hands a redactor <c>value:Tag</c>, so a bare key (<c>system.uptime:Key</c>) no longer reads as a key and
    /// is erased, and a pass-through renders the tag after the text. An app whose HMAC redactor wants a hash per tag turns
    /// it back on after this call.
    /// </remarks>
    /// <param name="logging">The app's logging builder.</param>
    /// <returns>The builder.</returns>
    public static ILoggingBuilder AddZabbixRedaction(this ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);
        logging.EnableRedaction(options => options.ApplyDiscriminator = false);
        logging.Services.AddRedaction(redaction =>
        {
            redaction.SetRedactor<ErasingRedactor>(ZabbixDataClassifications.Secret);
            redaction.SetRedactor<ZabbixItemKeyRedactor>(ZabbixDataClassifications.ItemKey);
            redaction.SetRedactor<ZabbixPeerTextRedactor>(ZabbixDataClassifications.PeerText);
            redaction.SetFallbackRedactor<ErasingRedactor>();
        });
        return logging;
    }
}
