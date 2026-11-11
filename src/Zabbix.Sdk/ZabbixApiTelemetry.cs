// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix;

/// <summary>
/// Provides the JSON-RPC API layer's traces and metrics: one client span and one <see cref="CallDuration"/> sample per
/// call, written by one Start/Finish pair so the two always agree, under <see cref="ZabbixTelemetryNames.Api"/>. An
/// application subscribes to every SDK layer with <c>AddSource(ZabbixTelemetryNames.Subscription)</c> / <c>AddMeter(ZabbixTelemetryNames.Subscription)</c> (<c>Zabbix.*</c>),
/// or to this one by <see cref="SourceName"/> and <see cref="MeterName"/>.
/// </summary>
/// <remarks>
/// Names follow the OpenTelemetry RPC semantic conventions (a release candidate): the span is named <c>{rpc.method}</c>; it and the duration carry <c>rpc.system.name</c> =
/// <c>jsonrpc</c>, <c>rpc.method</c>, <c>server.address</c> and <c>server.port</c>. A failure adds <c>error.type</c>: for an
/// <c>error</c> object the conventions ask for the returned status code, so it is the JSON-RPC code (<c>-32602</c>), also
/// set as <c>rpc.response.status_code</c>, with <c>zabbix.api.error.code</c> and the classified <c>zabbix.api.error.kind</c> beside it;
/// otherwise the well-known name from <see cref="ZabbixErrorType.Of"/> (<c>timeout</c> for a pipeline deadline,
/// <c>invalid_response</c> for a reply or result the SDK cannot read).
/// </remarks>
internal sealed class ZabbixApiTelemetry : ZabbixLayerTelemetry
{
    /// <summary>The activity source's name.</summary>
    public const string SourceName = ZabbixTelemetryNames.Api;

    /// <summary>The meter's name.</summary>
    public const string MeterName = ZabbixTelemetryNames.Api;

    /// <summary>The call duration histogram, in seconds.</summary>
    public const string CallDuration = "rpc.client.call.duration";

    /// <summary>The <c>rpc.system.name</c> value.</summary>
    public const string RpcSystem = "jsonrpc";

    private static readonly ActivitySource s_activities = new(SourceName, ScopeVersion(typeof(ZabbixApiTelemetry).Assembly));

    /// <summary>The layer, on the host's meter factory and clock.</summary>
    /// <param name="meters">The meter factory.</param>
    /// <param name="time">The clock durations are measured on.</param>
    public ZabbixApiTelemetry(IMeterFactory meters, TimeProvider time)
        : base(s_activities, MeterName, CallDuration, "The duration of a Zabbix JSON-RPC call, reply read and result deserialized.", meters, time)
    {
    }

    internal ZabbixOperation Start(string method, Uri endpoint) => Start(method, ActivityKind.Client, new TagList
    {
        { "rpc.system.name", RpcSystem },
        { "rpc.method", method },
        { "server.address", endpoint.Host },
        { "server.port", endpoint.Port },
    });

    internal void Finish(in ZabbixOperation operation, ZabbixApiException refusal)
    {
        string code = refusal.Code.ToString(CultureInfo.InvariantCulture);
        Finish(operation, refusal, errorType: code, extra: new TagList
        {
            { "rpc.response.status_code", code },
            { "zabbix.api.error.code", refusal.Code },
            { "zabbix.api.error.kind", KindName(refusal.Kind) },
        });
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "OpenTelemetry attribute values are snake_case by convention.")]
    private static string KindName(ZabbixApiErrorKind kind) => kind switch
    {
        ZabbixApiErrorKind.Unknown => "unknown",
        ZabbixApiErrorKind.Validation => "validation",
        ZabbixApiErrorKind.NotAuthorized => "not_authorized",
        ZabbixApiErrorKind.SessionTerminated => "session_terminated",
        ZabbixApiErrorKind.TokenExpired => "token_expired",
        ZabbixApiErrorKind.LoginFailed => "login_failed",
        ZabbixApiErrorKind.Permission => "permission",
        ZabbixApiErrorKind.PermissionOrNotFound => "permission_or_not_found",
        ZabbixApiErrorKind.MethodNotFound => "method_not_found",
        ZabbixApiErrorKind.AuthorizationHeaderRefused => "authorization_header_refused",
        ZabbixApiErrorKind.Transient => "transient",
        ZabbixApiErrorKind.Database => "database",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
