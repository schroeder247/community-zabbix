// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

namespace Community.Zabbix;

/// <summary>
/// The exception that is thrown when the trapper answers a batch with something other than a success. The reply's <c>response</c> was not <c>success</c>, or its
/// <c>info</c> line was not the counters upstream writes (<c>processed: N; failed: N; total: N; seconds spent: X</c>), so
/// nothing can be said to have been taken (zabbix_sender's <c>check_response</c> returns FAIL). A value the server
/// refused inside a successful reply is not this: it is <see cref="ZabbixSenderResponse.Failed"/>, data.
/// </summary>
public sealed class ZabbixTrapperRefusedException : ZabbixRefusedException
{
    /// <summary>A refusal with no detail.</summary>
    public ZabbixTrapperRefusedException()
    {
    }

    /// <summary>A refusal with a message.</summary>
    /// <param name="message">The message.</param>
    public ZabbixTrapperRefusedException(string message) : base(message)
    {
    }

    /// <summary>A refusal caused by another failure.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ZabbixTrapperRefusedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>A reply that is not a success.</summary>
    /// <param name="response">The reply's <c>response</c>.</param>
    /// <param name="info">The reply's <c>info</c>.</param>
    public ZabbixTrapperRefusedException(string? response, string? info)
        : base($"The trapper answered {response ?? "no response"}: {info ?? "no info"}")
    {
        Response = response;
        Info = info;
    }

    /// <summary>The reply's <c>response</c> word.</summary>
    public string? Response { get; }

    /// <summary>The reply's <c>info</c> line.</summary>
    public string? Info { get; }

    /// <summary>
    /// Whether the server refused because it has paused data collection (its values-per-second cap,
    /// <c>zbx_vps_monitor_capped</c>): it took nothing and will take the same values later, so they are held and sent
    /// again, not dropped. The two texts are upstream's own: <c>sender data</c> (<c>proxy.c</c> 8.0:1818) and
    /// <c>agent data</c> (<c>trapper.c</c> 8.0:195). "Zabbix server shutdown in progress" is not a pause: the server
    /// answers it after it processed the data, so sending again could store the values twice; it stays a refusal.
    /// </summary>
    public bool IsPaused => Info is "data collection has been paused" or "data collection is paused";
}
