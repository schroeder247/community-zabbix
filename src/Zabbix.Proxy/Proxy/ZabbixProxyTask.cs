// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Community.Zabbix.Proxy;

/// <summary>
/// One task the server hands a proxy back in a <c>proxy data</c>/<c>proxy config</c> acknowledgement's <c>tasks</c>
/// array (<c>tm_json_serialize_task</c> plus its per-type fields, <c>src/libs/zbxtasks/task.c</c>). Only the two kinds
/// a proxy is ever asked to execute are modeled: a remote command (<see cref="Command"/> present,
/// <c>ZBX_TM_TASK_REMOTE_COMMAND</c> = 2) and a forced item check (<see cref="ItemId"/> present,
/// <c>ZBX_TM_TASK_CHECK_NOW</c> = 6); any other <see cref="Type"/> carries only the common fields.
/// </summary>
/// <param name="Type">The task's own type (<c>ZBX_TM_TASK_*</c>).</param>
/// <param name="Clock">When the task was created.</param>
/// <param name="Ttl">How long the task is valid for, in seconds; 0 for none.</param>
/// <param name="ItemId">The item to check now (<c>ZBX_TM_TASK_CHECK_NOW</c>).</param>
/// <param name="CommandType">The remote command's kind (<c>ZBX_SCRIPT_TYPE_*</c>).</param>
/// <param name="Command">The remote command's text.</param>
/// <param name="ExecuteOn">Where to run it: 0 agent, 1 server/proxy, 2 proxy (<c>ZBX_SCRIPT_EXECUTE_ON_*</c>).</param>
/// <param name="Port">The agent port to run it on, when <paramref name="ExecuteOn"/> is the agent.</param>
/// <param name="AuthType">The connection's authentication type, for an SSH/Telnet script.</param>
/// <param name="Username">The connection's username.</param>
/// <param name="Password">The connection's password.</param>
/// <param name="PublicKey">The SSH public key file name.</param>
/// <param name="PrivateKey">The SSH private key file name.</param>
/// <param name="AlertId">The alert this command answers, when it was sent for one.</param>
/// <param name="HostId">The host the command targets.</param>
/// <param name="ParentTaskId">The task id to echo back in the result (<see cref="ZabbixProxyTaskResult.ParentTaskId"/>).</param>
public sealed record ZabbixProxyTask(
    [property: JsonPropertyName("type")] ZabbixProxyTaskType Type,
    [property: JsonPropertyName("clock"), JsonConverter(typeof(ZabbixUnixTimeConverter))] DateTimeOffset Clock,
    [property: JsonPropertyName("ttl")] long Ttl,
    [property: JsonPropertyName("itemid")] ulong? ItemId = null,
    [property: JsonPropertyName("command_type")] int? CommandType = null,
    [property: JsonPropertyName("command")] string? Command = null,
    [property: JsonPropertyName("execute_on")] int? ExecuteOn = null,
    [property: JsonPropertyName("port")] int? Port = null,
    [property: JsonPropertyName("authtype")] int? AuthType = null,
    [property: JsonPropertyName("username")] string? Username = null,
    [property: JsonPropertyName("password"), ZabbixSecret] string? Password = null,
    [property: JsonPropertyName("publickey")] string? PublicKey = null,
    [property: JsonPropertyName("privatekey")] string? PrivateKey = null,
    [property: JsonPropertyName("alertid")] ulong? AlertId = null,
    [property: JsonPropertyName("hostid")] ulong? HostId = null,
    [property: JsonPropertyName("parent_taskid")] ulong? ParentTaskId = null)
{
    // The text form (ToString, a debugger, a log line that formats the record) carries every member but the password.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Type = {Type}, Clock = {Clock}, Ttl = {Ttl}, ItemId = {ItemId}, CommandType = {CommandType}, ")
            .Append(CultureInfo.InvariantCulture, $"Command = {Command}, ExecuteOn = {ExecuteOn}, Port = {Port}, AuthType = {AuthType}, Username = {Username}, ")
            .Append(CultureInfo.InvariantCulture, $"PublicKey = {PublicKey}, PrivateKey = {PrivateKey}, AlertId = {AlertId}, HostId = {HostId}, ParentTaskId = {ParentTaskId}");
        return true;
    }
}
