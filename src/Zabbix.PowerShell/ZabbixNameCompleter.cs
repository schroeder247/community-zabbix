// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Language;
using Community.Zabbix.PowerShell.Diagnostics;
using Microsoft.Extensions.Caching.Memory;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Completes the names of one entity on the command's server that start with the word being completed — at most
/// <see cref="ZabbixCompletion.Limit"/> of them, asked for with <c>search</c>/<c>startSearch</c>/<c>limit</c> — kept per
/// server, entity and word in the context's <see cref="IMemoryCache"/> for <see cref="ZabbixCompletion.Lifetime"/>.
/// The fetch is bounded by <see cref="ZabbixCompletion.Deadline"/> through a <see cref="CancellationTokenSource"/> on
/// the clock, so a slow server
/// costs the user one short pause and the request is cancelled, not abandoned; a failure offers nothing, caches nothing,
/// and is logged (<see cref="ZabbixPowerShellLog.CompletionFailed"/>), surfacing as a warning on the next command.
/// </summary>
/// <typeparam name="TEntity">The entity.</typeparam>
internal sealed class ZabbixNameCompleter<TEntity>(ZabbixEntityShape<TEntity> shape) : IArgumentCompleter
{
    public IEnumerable<CompletionResult> CompleteArgument(string commandName, string parameterName, string wordToComplete, CommandAst commandAst, IDictionary fakeBoundParameters)
    {
        if (!ZabbixContext.TryGetCurrent(out ZabbixContext? context)) return [];
        ZabbixConnection? connection = fakeBoundParameters?["Connection"] switch
        {
            ZabbixConnection given => given,
            string name when context.Servers.TryGetValue(name, out ZabbixConnection? named) => named,
            _ => context.Current,
        };
        // A word with a wildcard completes the names it matches (Get-ZbxHost *web<Tab>), as the parameter would; the
        // server is asked with the API's one wildcard ('?' becomes '*', the answer is narrowed here).
        string prefix = (wordToComplete ?? string.Empty).Trim('\'', '"');
        if (connection is null || !TryNames(context, connection, prefix.Replace('?', '*'), out IReadOnlyList<KeyValuePair<string, string>>? names)) return [];

        ZabbixNamePattern? pattern = WildcardPattern.ContainsWildcardCharacters(prefix) ? ZabbixNamePattern.Parse(prefix + "*") : null;
        return names
            .Where(pair => pattern?.IsMatch(pair.Key) ?? pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(pair => new CompletionResult(ZabbixCompletion.Quote(pair.Key), pair.Key, CompletionResultType.ParameterValue, $"{pair.Key} ({shape.IdField} {pair.Value})"));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The deadline and the pipeline are owned by the fetch and disposed by its continuation when it ends, which may be after this method returns (a completion that gave up at its deadline); the analyzer cannot see the transfer.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The completer bridge: CompleteArgument is synchronous, so the fetch starts off the calling thread in an execution context of its own (decisions.md, \"Async meets a synchronous contract at three places\").")]
    private bool TryNames(ZabbixContext context, ZabbixConnection connection, string start, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReadOnlyList<KeyValuePair<string, string>>? names)
    {
        var key = (connection, shape.Api, start.ToUpperInvariant());
        if (context.Completions.TryGetValue(key, out names) && names is not null) return true;
        if (!connection.TryLease())
        {
            names = null;
            return false;
        }

        // The completer contract is synchronous (CompleteArgument returns the results), so the fetch is handed back
        // through the same blocking edge the cmdlets drain (ZabbixPipeline), bounded by the deadline. The fetch does
        // not enter the pipeline: its log lines are nobody's command's, and wait for the next one.
        // On the runspace's clock (ZabbixContext.Time), as every command of the context runs.
        // Owned by the fetch, not by this frame: a completion that gives up at the deadline returns while the fetch is
        // still unwinding its cancelled request, so both are disposed when the fetch ends (below), never under it.
        var deadline = new CancellationTokenSource(ZabbixCompletion.Deadline, context.Time);
        var pipeline = new ZabbixPipeline(verbose: false, debug: false, information: false, warning: false);
        Task fetch = Task.Run(async () =>
        {
            try
            {
                IReadOnlyList<TEntity> found = await shape.SelectStartingWithAsync(connection.Client, start, ZabbixCompletion.Limit, deadline.Token).ConfigureAwait(false);
                pipeline.Post(new OutputWrite((IReadOnlyList<KeyValuePair<string, string>>)[.. found
                    .Select(entity => new KeyValuePair<string, string>(shape.NameOf(entity) ?? string.Empty, shape.IdOf(entity)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty))
                    .Where(pair => pair.Key.Length > 0)
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)]));
            }
            catch (Exception failure) when (Report(connection, failure))
            {
                // Reported; no names, nothing cached.
            }
            finally
            {
                connection.Release();
                pipeline.Complete();
            }
        });

        try
        {
            names = Receive(pipeline, deadline.Token);
        }
        finally
        {
            // Whatever ended the wait, the fetch ends with it; it never faults (every failure is reported inside it).
            deadline.Cancel();
            _ = DisposeAfterAsync(fetch, deadline, pipeline);
        }
        if (names is null) return false;
        context.Completions.Set(key, names, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ZabbixCompletion.Lifetime, Size = 1 });
        return true;
    }

    // The fetch never faults (every failure is reported inside it), so this awaits its end and nothing else.
    private static async Task DisposeAfterAsync(Task fetch, CancellationTokenSource deadline, ZabbixPipeline pipeline)
    {
        await fetch.ConfigureAwait(false);
        deadline.Dispose();
        pipeline.Dispose();
    }

    private static IReadOnlyList<KeyValuePair<string, string>>? Receive(ZabbixPipeline pipeline, CancellationToken deadline)
    {
        IReadOnlyList<KeyValuePair<string, string>>? names = null;
        try
        {
            foreach (PipelineWrite write in pipeline.Drain(deadline))
            {
                if (write is OutputWrite { Value: IReadOnlyList<KeyValuePair<string, string>> fetched }) names = fetched;
            }
        }
        catch (OperationCanceledException)
        {
            // Past the deadline: the fetch was cancelled with it; offer nothing now.
        }
        return names;
    }

    private bool Report(ZabbixConnection connection, Exception failure)
    {
        ZabbixPowerShellLog.CompletionFailed(connection.Log, shape.Api, connection.Name, failure);
        return true;
    }
}
