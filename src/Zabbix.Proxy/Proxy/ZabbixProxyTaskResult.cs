// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Proxy;

/// <summary>
/// One row of a <c>tasks</c> acknowledgement: the proxy hands back what it did with a task the server gave it
/// (<c>tm_json_serialize_task</c> + <c>tm_json_serialize_remote_command_result</c>, <c>src/libs/zbxtasks/task.c</c>). This
/// SDK does not execute remote commands; a caller that accepts one from <see cref="ZabbixProxyResponse.Tasks"/> and runs it
/// elsewhere reports the outcome back through this shape.
/// </summary>
/// <param name="Type">The task's own type, echoed back (<see cref="ZabbixProxyTaskType.RemoteCommandResult"/>).</param>
/// <param name="Clock">When the result was produced.</param>
/// <param name="Ttl">Echoed from the task; 0 when the task carried none.</param>
/// <param name="Status">0 success, 1 failure.</param>
/// <param name="Info">The command's output or the failure reason.</param>
/// <param name="ParentTaskId">The task this is the result of.</param>
public sealed record ZabbixProxyTaskResult(
    [property: JsonPropertyName("type")] ZabbixProxyTaskType Type,
    [property: JsonPropertyName("clock"), JsonConverter(typeof(ZabbixUnixTimeConverter))] DateTimeOffset Clock,
    [property: JsonPropertyName("ttl")] long Ttl,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("info")] string? Info,
    [property: JsonPropertyName("parent_taskid")] ulong ParentTaskId)
;
