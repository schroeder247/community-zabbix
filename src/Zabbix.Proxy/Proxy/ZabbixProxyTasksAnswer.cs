// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Proxy;

/// <summary>
/// What a passive proxy answers the server's <c>proxy tasks</c> with (<c>trapper_proxy.c</c> <c>send_task_data</c>, the same
/// in 7.0, 7.4 and 8.0): the task results it holds, its version and its clock. The version is what the server rates the
/// proxy by (<c>proxypoller.c</c> <c>proxy_get_tasks</c>, 8.0:520-548): an empty answer reads as a proxy older than 3.4.
/// </summary>
/// <param name="Tasks">Task results for the server; absent when none are held.</param>
/// <param name="Version">The proxy's version.</param>
/// <param name="Clock">Unix seconds.</param>
/// <param name="Ns">Nanoseconds within the second.</param>
internal sealed record ZabbixProxyTasksAnswer(
    [property: JsonPropertyName("tasks")] IReadOnlyList<ZabbixProxyTaskResult>? Tasks,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("clock")] long Clock,
    [property: JsonPropertyName("ns")] int Ns);
