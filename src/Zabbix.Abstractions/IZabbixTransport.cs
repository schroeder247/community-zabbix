// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix;

/// <summary>
/// Sends one JSON-RPC request and reads its reply: a direct <c>await</c> through the HTTP handler chain, never a message
/// through a bounded block. Parameters and results travel with their
/// <see cref="JsonTypeInfo{T}"/>, so the transport serialises nothing by reflection and is trim- and AOT-safe.
/// </summary>
/// <remarks>
/// Throws <see cref="ZabbixApiException"/> for an <c>error</c> object (its <see cref="ZabbixApiException.Kind"/> says
/// what it means); <see cref="HttpRequestException"/> for a failed exchange, including one the resilience pipeline did
/// not send because its circuit is open or its concurrency limit is reached; <see cref="InvalidDataException"/> for a
/// reply that does not answer the request it was sent for; <see cref="System.Text.Json.JsonException"/> for a result
/// the model cannot read; <see cref="TimeoutException"/> when the pipeline's attempt or total deadline passes (the
/// headers or the body did not arrive in time; <see cref="ZabbixClientOptions.Timeouts"/> sets it per method); and
/// <see cref="OperationCanceledException"/> when the caller cancels. A resilience pipeline's own exception, where there
/// is one, is the inner exception.
/// <para>A call returns a <see cref="Task{TResult}"/>: every call crosses the network, so a <c>ValueTask</c> would save
/// nothing.</para>
/// </remarks>
public interface IZabbixTransport
{
    /// <summary>Call <paramref name="method"/> and read its <c>result</c> as <typeparamref name="TResult"/>.</summary>
    /// <typeparam name="TParameters">The parameters' type.</typeparam>
    /// <typeparam name="TResult">The result's type.</typeparam>
    /// <param name="method">The API method, e.g. <c>host.get</c>.</param>
    /// <param name="parameters">The method's parameters, written as the request's <c>params</c>.</param>
    /// <param name="parametersInfo">The type information the parameters are serialized with.</param>
    /// <param name="resultInfo">The type information the <c>result</c> is read with.</param>
    /// <param name="cancellationToken">A cancellation token, which reaches the socket.</param>
    /// <returns>The call's <c>result</c>, read as <typeparamref name="TResult"/>.</returns>
    Task<TResult> SendAsync<TParameters, TResult>(
        string method,
        TParameters parameters,
        JsonTypeInfo<TParameters> parametersInfo,
        JsonTypeInfo<TResult> resultInfo,
        CancellationToken cancellationToken = default);

    /// <summary>Call <paramref name="method"/> and hand back its raw <c>result</c> — the escape hatch.</summary>
    /// <typeparam name="TParameters">The parameters' type.</typeparam>
    /// <param name="method">The API method, e.g. <c>host.get</c>.</param>
    /// <param name="parameters">The method's parameters, written as the request's <c>params</c>.</param>
    /// <param name="parametersInfo">The type information the parameters are serialized with.</param>
    /// <param name="cancellationToken">A cancellation token, which reaches the socket.</param>
    /// <returns>The call's <c>result</c>, as the server answered it.</returns>
    Task<JsonElement> SendAsync<TParameters>(
        string method,
        TParameters parameters,
        JsonTypeInfo<TParameters> parametersInfo,
        CancellationToken cancellationToken = default);
}
