// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;
using System.Reflection;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Provides the base of a command that talks to a connected server: the current server of the runspace's context, or
/// the one named (or given) by <c>-Connection</c>. It keeps no state of its own; each call holds a lease on the
/// connection so a reconnect in this or another runspace cannot dispose it mid-call.
/// </summary>
public abstract class ZabbixServerCmdlet : ZabbixCmdlet
{
    private ZabbixConnection? _connection;

    /// <summary>The server to talk to: a connection name from <c>Get-ZbxContext</c>, or the connection object. The current server when omitted.</summary>
    [Parameter]
    [ZabbixConnectionTransformation]
    [ZabbixConnectionCompleter]
    public ZabbixConnection? Connection { get; set; }

    /// <summary>The connection this command talks to; a terminating error when the session is not connected.</summary>
    protected ZabbixConnection Connected => _connection ??= Connection ?? ZabbixContext.Current ?? NotConnected();

    /// <summary>
    /// The SDK root of <see cref="Connected"/>. Named after its type (as <see cref="ZabbixCmdlet.ZabbixContext"/> is), so a
    /// command can keep a <c>-Server</c> parameter (a trapper's address) without hiding it.
    /// </summary>
    protected ZabbixClient ZabbixClient => Connected.Client;

    /// <summary>
    /// Whether this command needs a connection before it runs. A command that can do its work without the API (a
    /// sender given its trapper) answers <see langword="false"/> for that invocation; it then runs on the connection
    /// only when one exists.
    /// </summary>
    private protected virtual bool NeedsConnection => true;

    /// <summary>The connection this command would talk to, or <see langword="null"/> when the runspace has none.</summary>
    private protected ZabbixConnection? ConnectedOrNull => _connection ?? Connection ?? ZabbixContext.Current;

    /// <inheritdoc/>
    private protected override ZabbixConnection? RunConnection => NeedsConnection ? Connected : ConnectedOrNull;

    /// <summary>
    /// Requires a connection before anything else runs — including a derived <c>New-</c>/<c>Set-</c>/<c>Remove-</c>
    /// cmdlet's own <see cref="Cmdlet.ShouldProcess(string, string)"/>/<see cref="Cmdlet.ShouldContinue(string, string, ref bool, ref bool)"/>
    /// prompts in <see cref="ZabbixCmdlet.ShouldChange"/>. Without this, a write cmdlet with no ambient connection would
    /// prompt first and only discover it has nothing to talk to afterward — in a host with no interactive console
    /// (a CI runner, a non-interactive Pester suite) <see cref="Cmdlet.ShouldContinue(string, string, ref bool, ref bool)"/>
    /// itself throws a bare <see cref="NullReferenceException"/> from
    /// <c>Microsoft.PowerShell.ConsoleHostUserInterface.PromptForChoice</c> before the connection is ever checked.
    /// Checking here, in <see cref="Cmdlet.BeginProcessing"/>, means every derived cmdlet gets the same
    /// <c>ZabbixNotConnected</c> terminating error <see cref="Connected"/> already gives <c>Get-ZbxHost</c>, before
    /// any prompt or null-reference has a chance to happen.
    /// </summary>
    protected override void BeginProcessing()
    {
        if (NeedsConnection) _ = Connected;
    }

    /// <summary>
    /// Resolves references to ids in one <c>.get</c> for all the names among them. A name that matches nothing is written
    /// as a non-terminating <c>ObjectNotFound</c> error and left out; a name that matches more than one object (item names
    /// repeat across hosts) is written as <c>ZabbixReferenceAmbiguous</c> and left out — a name never picks one of
    /// several objects to change, so the wrong object is never changed or deleted. Call it inside an operation.
    /// </summary>
    /// <typeparam name="TEntity">The object referred to.</typeparam>
    /// <param name="references">The references.</param>
    /// <param name="cancellationToken">The operation's token.</param>
    protected async Task<IReadOnlyList<ulong>> ResolveAsync<TEntity>(IReadOnlyList<ZabbixReference<TEntity>> references, CancellationToken cancellationToken)
        => [.. (await ResolveTargetsAsync(references, cancellationToken).ConfigureAwait(false)).Select(target => target.Id)];

    /// <summary>
    /// <see cref="ResolveAsync{TEntity}"/>, each id with the text a confirmation names its object by (one
    /// <c>ShouldProcess</c> per resolved object, never "3 host(s)" before resolution): the API object, its name when the
    /// reference carries one (given by name, or piped from a <c>Get-</c>), and its id — <c>host 'web-01' (10084)</c>.
    /// Call it inside an operation; confirm on the pipeline thread after it returns.
    /// </summary>
    /// <typeparam name="TEntity">The object referred to.</typeparam>
    /// <param name="references">The references.</param>
    /// <param name="cancellationToken">The operation's token.</param>
    internal async Task<IReadOnlyList<(ulong Id, string Target)>> ResolveTargetsAsync<TEntity>(IReadOnlyList<ZabbixReference<TEntity>> references, CancellationToken cancellationToken)
    {
        (IReadOnlyList<(ulong Id, string Target)> resolved, IReadOnlyList<ErrorRecord> failures) = await ResolveCoreAsync(references, cancellationToken).ConfigureAwait(false);
        foreach (ErrorRecord failure in failures) ZabbixPipeline.Current?.Post(new ErrorWrite(failure));
        return resolved;
    }

    /// <summary>
    /// Resolves the references a request field carries (<c>Set-ZbxHost -Template</c>, <c>-Group</c>): all of them, or
    /// the change does not happen. Unlike the objects a command acts on — where one name that matches nothing leaves
    /// that object out and the rest go ahead — a field sent with the part that resolved REPLACES the field with less than
    /// was asked (<c>-Template 'typo'</c> would unlink every template). Call it inside an operation.
    /// </summary>
    /// <typeparam name="TEntity">The object referred to.</typeparam>
    /// <param name="references">The field's references.</param>
    /// <param name="parameter">The parameter that carries them, for the error.</param>
    /// <param name="cancellationToken">The operation's token.</param>
    /// <exception cref="ZabbixParameterException">A reference matched nothing, or more than one object.</exception>
    protected async Task<IReadOnlyList<ulong>> ResolveFieldAsync<TEntity>(IReadOnlyList<ZabbixReference<TEntity>> references, string parameter, CancellationToken cancellationToken)
    {
        (IReadOnlyList<(ulong Id, string Target)> resolved, IReadOnlyList<ErrorRecord> failures) = await ResolveCoreAsync(references, cancellationToken).ConfigureAwait(false);
        if (failures.Count > 0)
        {
            throw new ZabbixParameterException(failures[0].FullyQualifiedErrorId, parameter,
                $"-{parameter}: {string.Join("; ", failures.Select(failure => failure.Exception.Message))}; nothing was changed");
        }
        return [.. resolved.Select(target => target.Id)];
    }

    /// <summary>
    /// The objects of <paramref name="targets"/> the user lets <paramref name="action"/> change: one
    /// <see cref="ZabbixCmdlet.ShouldProcessUnlessForced"/> per object, named by its target text, so <c>-WhatIf</c> lists
    /// each object and <c>-Confirm</c> asks for each (Yes to All answers the rest). The change itself is one call for the
    /// confirmed ones. A change that warrants no second question takes only this: <c>ConfirmImpact</c>
    /// decides whether it prompts by default, and a host that cannot prompt runs it as PowerShell's own cmdlets do.
    /// </summary>
    /// <param name="targets">The resolved objects (<see cref="ResolveTargetsAsync{TEntity}"/>).</param>
    /// <param name="action">The change, as the prompt says it.</param>
    /// <param name="force">Whether <c>-Force</c> was given.</param>
    internal IReadOnlyList<ulong> Confirmed(IEnumerable<(ulong Id, string Target)> targets, string action, bool force)
        => [.. targets.DistinctBy(target => target.Id).Where(target => ShouldProcessUnlessForced(target.Target, action, force)).Select(target => target.Id)];

    /// <summary>
    /// The shape of every command that changes a set of objects in one call: resolves <paramref name="references"/>
    /// (<see cref="ResolveTargetsAsync{TEntity}"/>, one <c>.get</c> for the names), then confirms each resolved object by
    /// name (<see cref="Confirmed"/>), so <c>-WhatIf</c> lists every object and never "3 user(s)". Runs on the pipeline
    /// thread; <see langword="false"/> when nothing is left to change (none given, none resolved, none confirmed, or the
    /// resolution failed and wrote its error).
    /// </summary>
    /// <typeparam name="TEntity">The object changed.</typeparam>
    /// <param name="references">The references gathered from the pipeline.</param>
    /// <param name="action">The change, as the prompt says it (<c>Unblock user</c>).</param>
    /// <param name="force">Whether <c>-Force</c> was given.</param>
    /// <param name="confirmed">The confirmed ids.</param>
    internal bool TryConfirm<TEntity>(IReadOnlyList<ZabbixReference<TEntity>> references, string action, bool force, out IReadOnlyList<ulong> confirmed)
    {
        confirmed = [];
        if (references.Count == 0) return false;
        if (!TryRun(cancellationToken => ResolveTargetsAsync(references, cancellationToken), out IReadOnlyList<(ulong Id, string Target)>? targets, references.ToArray())) return false;
        confirmed = Confirmed(targets, action, force);
        return confirmed.Count > 0;
    }

    private async Task<(IReadOnlyList<(ulong Id, string Target)> Resolved, IReadOnlyList<ErrorRecord> Failures)> ResolveCoreAsync<TEntity>(IReadOnlyList<ZabbixReference<TEntity>> references, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);
        var failures = new List<ErrorRecord>();
        // Names compare case-insensitively (the shape selects them so, as a Get- -Name does): 'web01' names WEB01, and two
        // objects that differ only in case are two matches, so the name is ambiguous, never one of them picked.
        string[] names = [.. references.Where(reference => reference.Id is null).Select(reference => reference.Name!).Distinct(StringComparer.OrdinalIgnoreCase)];
        Dictionary<string, List<ulong>> found = new(StringComparer.OrdinalIgnoreCase);
        ZabbixEntityShape<TEntity> shape = ZabbixEntities.Of<TEntity>();
        if (names.Length > 0)
        {
            foreach (TEntity entity in await shape.SelectByNameAsync(ZabbixClient, names, cancellationToken).ConfigureAwait(false))
            {
                if (shape.NameOf(entity) is { } name && shape.IdOf(entity) is { } id)
                {
                    if (!found.TryGetValue(name, out List<ulong>? ids)) found[name] = ids = [];
                    if (!ids.Contains(id)) ids.Add(id);
                }
            }
        }

        var resolved = new List<(ulong Id, string Target)>(references.Count);
        string Target(ulong id, string? name) => name is null ? $"{shape.Api} {id}" : $"{shape.Api} '{name}' ({id})";
        foreach (ZabbixReference<TEntity> reference in references)
        {
            if (reference.Id is { } id)
            {
                resolved.Add((id, Target(id, reference.Name)));
            }
            else if (found.TryGetValue(reference.Name!, out List<ulong>? ids) && ids.Count == 1)
            {
                resolved.Add((ids[0], Target(ids[0], reference.Name)));
            }
            else if (ids is { Count: > 1 })
            {
                var ambiguous = new AmbiguousMatchException(
                    $"{ids.Count} {shape.Noun} objects on {Connected.Name} are named '{reference.Name}' ({string.Join(", ", ids)}): give the id or the object");
                failures.Add(new ErrorRecord(ambiguous, "ZabbixReferenceAmbiguous", ErrorCategory.InvalidArgument, reference.Name));
            }
            else
            {
                var missing = new ItemNotFoundException($"no {shape.Noun} is named '{reference.Name}' on {Connected.Name}");
                failures.Add(new ErrorRecord(missing, "ZabbixReferenceNotFound", ErrorCategory.ObjectNotFound, reference.Name));
            }
        }
        return (resolved, failures);
    }

    /// <summary>
    /// The objects a generated client's <c>GetAsync</c> answers, as a list, whichever shape the client has (a stream or
    /// a list): the same call compiles against both, so an operation that needs the objects (not the pipeline) survives
    /// the generator's shape switch as a recompile, as <see cref="ZabbixCmdlet.WriteAll{T}(IAsyncEnumerable{T}, object?)"/> does.
    /// </summary>
    /// <typeparam name="T">The object.</typeparam>
    /// <param name="source">The client's answer.</param>
    /// <param name="cancellationToken">The operation's token.</param>
    protected static async Task<IReadOnlyList<T>> ListAsync<T>(IAsyncEnumerable<T> source, CancellationToken cancellationToken)
        => await source.ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc cref="ListAsync{T}(IAsyncEnumerable{T}, CancellationToken)"/>
    protected static Task<IReadOnlyList<T>> ListAsync<T>(Task<IReadOnlyList<T>> source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// The server's trapper, as an ordered list of nodes (one HA cluster, tried in turn on failover):
    /// <paramref name="given"/>, else the connection's (<c>Connect-ZbxServer -Trapper</c>), else the active HA node's
    /// address and port from <c>hanode.get</c> — the frontend's own rule (<c>ZBase.php</c>: <c>$ZBX_SERVER</c>, else
    /// the node whose status is active). Never the API's host with a guessed port. Call it inside an operation.
    /// </summary>
    /// <param name="given">The command's <c>-Server</c>, if any.</param>
    /// <param name="cancellationToken">The operation's token.</param>
    /// <returns>The nodes to try, in order; never empty.</returns>
    /// <exception cref="InvalidOperationException">No trapper is known and the API names no active node.</exception>
    protected async Task<IReadOnlyList<DnsEndPoint>> ResolveTrapperAsync(IReadOnlyList<DnsEndPoint>? given, CancellationToken cancellationToken)
    {
        if (given is { Count: > 0 }) return given;
        if (Connected.Trapper is { Count: > 0 } configured) return configured;
        var active = new ZabbixHANodeGetParameters
        {
            Output = [ZabbixHANodeFields.Address, ZabbixHANodeFields.Port, ZabbixHANodeFields.Status],
            Filter = new() { ["status"] = (long)ZabbixHANodeStatus.Active },
        };
        foreach (ZabbixHANode ha in await ListAsync(ZabbixClient.HANodes.GetAsync(active, cancellationToken), cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(ha.Address)) return [new DnsEndPoint(ha.Address, ha.Port?.Number ?? ZabbixWireClient.DefaultServerPort)];
        }
        throw new InvalidOperationException(
            $"The trapper of {Connected.Name} is not known and hanode.get names no active node: pass -Server host:port, or Connect-ZbxServer -Trapper host:port.");
    }

    private ZabbixConnection NotConnected()
    {
        ThrowTerminatingError(new ErrorRecord(
            new InvalidOperationException("not connected to a Zabbix server: run Connect-ZbxServer first, or pass -Connection"),
            "ZabbixNotConnected", ErrorCategory.ConnectionError, null));
        return null!;
    }
}
