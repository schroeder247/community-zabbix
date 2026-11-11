// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Answers an item key through the built-in keys (<c>agent.ping</c>, <c>agent.version</c>,
/// <c>agent.hostname</c>, <c>system.uname</c> and the bundled items, Zabbix's own <c>system.*</c>, <c>proc.*</c>,
/// <c>vfs.*</c>, <c>net.*</c> and <c>web.*</c> keys) — no session needed — or, with <c>-Session</c>, through that
/// running session's full handler set: every scriptblock registered on it by <c>Register-ZbxAgentHandler</c> and its
/// kind-checked wrappers, the keys of its plugins, and the built-in keys, as its key rules allow.</para>
/// <para type="description">
/// Writes one <c>ZabbixAgentAnswer</c> per key
/// (or, with <c>-Raw</c>, just its value or error text), and no error: a key no handler supports is an answer whose
/// <c>Error</c> is "This agent does not answer the key.", as the agent itself would answer. A key that does not parse is refused by
/// the binder (<c>-Key</c> is a <c>ZabbixItemKey</c>); a <c>-Session</c> that is not running ends the command
/// (<c>ZabbixAgentNotFound</c>).
/// </para>
/// </summary>
/// <remarks>
/// With <c>-Session</c>, the key goes through the live <see cref="ZabbixAgentSession.Handlers"/> and the
/// <see cref="ZabbixItemDictionary.AnswerAsync(ZabbixItemKey, TimeSpan, CancellationToken)"/> contract, on the session's clock; without it, through <see cref="ZabbixItemDictionary.BuiltIn"/>'s
/// same contract.
/// </remarks>
[Cmdlet(VerbsDiagnostic.Test, "ZbxAgentItem", DefaultParameterSetName = ParameterSets.Answer)]
[OutputType(typeof(ZabbixAgentAnswer), ParameterSetName = [ParameterSets.Answer])]
[OutputType(typeof(string), ParameterSetName = [ParameterSets.Raw])]
public sealed class TestZbxAgentItemCommand : ZabbixCmdlet
{
    /// <summary>The item key to dispatch.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    public ZabbixItemKey Key { get; set; }

    /// <summary>What <c>agent.hostname</c> answers; ignored with <c>-Session</c> (the session's own hostname applies).</summary>
    [Parameter]
    [ValidateNotNullOrWhiteSpace]
    public string HostName { get; set; } = Environment.MachineName;

    /// <summary>Dispatches through this running agent session's full handler set (scriptblocks, plugins and built-in keys, as its <c>-KeyRule</c> allows) instead of the built-in keys alone.</summary>
    [Parameter]
    [ValidateNotNullOrWhiteSpace]
    public string? Session { get; set; }

    /// <summary>How long the handler may take: Zabbix's time suffix (<c>'5s'</c>), seconds or a time span.</summary>
    [Parameter]
    [ZabbixDuration]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Write the bare answer value (or error text) instead of the whole diagnostic <c>ZabbixAgentAnswer</c>.
    /// </summary>
    [Parameter(Mandatory = true, ParameterSetName = ParameterSets.Raw)]
    public SwitchParameter Raw { get; set; }

    private ZabbixItemDictionary? _handlers;

    /// <inheritdoc/>
    protected override void BeginProcessing()
    {
        if (Session is null)
        {
            _handlers = ZabbixItemDictionary.BuiltIn(HostName);
        }
        else if (ZabbixContext.Sessions.TryGet(Session, out ZabbixAgentSession? session))
        {
            _handlers = session.Handlers;
        }
        else
        {
            ThrowTerminatingError(new ErrorRecord(new ItemNotFoundException($"no agent session is named '{Session}'"), "ZabbixAgentNotFound", ErrorCategory.ObjectNotFound, Session));
        }
    }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        ZabbixItemKey key = Key;
        if (TryRun(cancellationToken => _handlers!.AnswerAsync(key, Timeout, cancellationToken).AsTask(), out ZabbixAgentAnswer? answer, key))
            WriteObject(Raw.IsPresent ? answer.Value ?? answer.Error : answer);
    }

    private static class ParameterSets
    {
        public const string Answer = "Answer";
        public const string Raw = "Raw";
    }
}
