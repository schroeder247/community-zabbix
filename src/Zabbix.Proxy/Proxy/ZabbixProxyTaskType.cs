// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Proxy;

/// <summary>
/// A task's type (upstream's <c>ZBX_TM_TASK_*</c>, <c>zbxtasks.h</c>). A proxy is handed <see cref="RemoteCommand"/> and
/// <see cref="CheckNow"/> and answers a remote command with a <see cref="RemoteCommandResult"/>; a type a later server
/// adds reads as its number.
/// </summary>
public enum ZabbixProxyTaskType
{
    /// <summary><c>ZBX_TM_TASK_UNDEFINED</c>: no type was sent.</summary>
    Undefined = 0,

    /// <summary><c>ZBX_TM_TASK_CLOSE_PROBLEM</c>.</summary>
    CloseProblem = 1,

    /// <summary><c>ZBX_TM_TASK_REMOTE_COMMAND</c>: run a command; the task carries it.</summary>
    RemoteCommand = 2,

    /// <summary><c>ZBX_TM_TASK_REMOTE_COMMAND_RESULT</c>: what a remote command did, sent back.</summary>
    RemoteCommandResult = 3,

    /// <summary><c>ZBX_TM_TASK_ACKNOWLEDGE</c>.</summary>
    Acknowledge = 4,

    /// <summary><c>ZBX_TM_TASK_UPDATE_EVENTNAMES</c>.</summary>
    UpdateEventNames = 5,

    /// <summary><c>ZBX_TM_TASK_CHECK_NOW</c>: collect an item now; the task carries its id.</summary>
    CheckNow = 6,

    /// <summary><c>ZBX_TM_TASK_DATA</c>.</summary>
    Data = 7,

    /// <summary><c>ZBX_TM_TASK_DATA_RESULT</c>.</summary>
    DataResult = 8,
}
