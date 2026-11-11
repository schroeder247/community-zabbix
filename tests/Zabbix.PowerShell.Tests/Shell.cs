// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>One runspace with the module imported through its manifest, plus the test-only cmdlets below.</summary>
internal sealed class Shell : IDisposable
{
    private Shell(Runspace runspace) => Runspace = runspace;

    /// <summary>
    /// An engine hosted in this test process has no pwsh's configuration beside it, so on Windows its execution policy
    /// is the default that refuses a module's types and format files. The process-scope variable is what
    /// <c>pwsh -ExecutionPolicy</c> sets, and it reaches every runspace here, the module's own included.
    /// </summary>
    [ModuleInitializer]
    internal static void AllowTheModulesFiles() => Environment.SetEnvironmentVariable("PSExecutionPolicyPreference", "Bypass");

    public Runspace Runspace { get; }

    /// <summary>The module Community.Zabbix's manifest, beside the module assembly in the build output.</summary>
    public static string Manifest => ModuleUnderTest.Zabbix.Manifest;

    /// <summary>
    /// A runspace with all three modules imported, Community.Zabbix first (Community.Zabbix.Agent and Community.Zabbix.Proxy require it; an already
    /// imported module satisfies RequiredModules), as one folder: this project's output carries the three side by side.
    /// </summary>
    public static Shell Open()
    {
        var state = InitialSessionState.CreateDefault2();
        state.ImportPSModule([.. ModuleUnderTest.All.Select(module => module.Manifest)]);
        foreach (Type type in new[] { typeof(RemoveZbxTestHostCommand), typeof(InvokeZbxTestLogCommand), typeof(InvokeZbxTestStreamCommand), typeof(ResolveZbxTestTrapperCommand), typeof(InvokeZbxTestShouldChangeCommand) })
        {
            CmdletAttribute cmdlet = type.GetCustomAttributes(typeof(CmdletAttribute), false).Cast<CmdletAttribute>().Single();
            state.Commands.Add(new SessionStateCmdletEntry($"{cmdlet.VerbName}-{cmdlet.NounName}", type, null));
        }
        Runspace runspace = RunspaceFactory.CreateRunspace(state);
        // Bounded like every other wait here: opening imports the module.
        if (!Task.Run(runspace.Open).Wait(CloseDeadline)) throw new TimeoutException($"the runspace did not open within {CloseDeadline}");
        return new Shell(runspace);
    }

    public ZabbixContext Context => ZabbixContext.Of(Runspace);

    public System.Management.Automation.PowerShell Create(string script, IDictionary<string, object?>? variables = null)
    {
        foreach ((string name, object? value) in variables ?? new Dictionary<string, object?>()) Runspace.SessionStateProxy.SetVariable(name, value);
        var shell = System.Management.Automation.PowerShell.Create(Runspace);
        shell.AddScript(script);
        return shell;
    }

    /// <summary>
    /// How long any one script, or closing the runspace, may take before the test fails naming it: generous (the 100k
    /// pipelines take seconds), but a bound: a wait that can never end hangs the whole test run, and a test must fail with
    /// its name, not stall the run.
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How long closing (or opening) a runspace, or stopping a <see cref="Call"/> at the end of its test, may take: both
    /// take milliseconds, so a minute is only ever a stuck wait, and the test fails naming it rather than stalling the run
    /// (the stop-and-dispose of a test that has already failed is otherwise an unbounded wait).
    /// </summary>
    public static readonly TimeSpan CloseDeadline = TimeSpan.FromMinutes(1);

    public Run Invoke(string script, IDictionary<string, object?>? variables = null)
    {
        System.Management.Automation.PowerShell shell = Create(script, variables);
        Collection<PSObject> output = Invoke(shell, script);
        try
        {
            return new Run(output, [.. shell.Streams.Error], [.. shell.Streams.Warning.Select(w => w.Message)],
                [.. shell.Streams.Information], [.. shell.Streams.Debug.Select(d => d.Message)], [.. shell.Streams.Verbose.Select(v => v.Message)],
                [.. shell.Streams.Progress]);
        }
        finally
        {
            shell.Dispose();
        }
    }

    /// <summary>
    /// Runs a pipeline a test composed itself (<c>AddCommand</c>…), within <see cref="Deadline"/>; past it the pipeline
    /// is asked to stop, left undisposed (disposing a pipeline that does not stop blocks for as long as it does not), and
    /// the test fails naming <paramref name="what"/>.
    /// </summary>
    public static Collection<PSObject> Invoke(System.Management.Automation.PowerShell pipeline, string what)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        IAsyncResult running = pipeline.BeginInvoke();
        if (!running.AsyncWaitHandle.WaitOne(Deadline))
        {
            pipeline.BeginStop(null, null);
            throw new TimeoutException($"the script did not finish within {Deadline}: {what}");
        }
        return [.. pipeline.EndInvoke(running)];
    }

    /// <summary>Starts <paramref name="script"/> without waiting for it: a test that stops it, or runs it beside another.</summary>
    public Call Start(string script, IDictionary<string, object?>? variables = null) => new(Create(script, variables), script, CloseDeadline);

    /// <summary><see cref="Start(string, IDictionary{string, object?}?)"/> with its own stop deadline (the bound's own test).</summary>
    public Call Start(string script, IDictionary<string, object?> variables, TimeSpan stopDeadline) => new(Create(script, variables), script, stopDeadline);

    /// <summary>
    /// Tab completion of <paramref name="input"/> in this runspace (<see cref="CommandCompletion.CompleteInput(string, int, Hashtable, System.Management.Automation.PowerShell)"/>
    /// runs a pipeline, and a completer may call the server), within <see cref="Deadline"/>.
    /// </summary>
    public CommandCompletion Complete(string input)
    {
        var pipeline = System.Management.Automation.PowerShell.Create(Runspace);
        Task<CommandCompletion> completing = Task.Run(() => CommandCompletion.CompleteInput(input, input.Length, null, pipeline));
        if (!completing.Wait(Deadline))
        {
            pipeline.BeginStop(null, null);
            throw new TimeoutException($"completion did not finish within {Deadline}: {input}");
        }
        pipeline.Dispose();
        return completing.Result;
    }

    /// <summary>Connects the runspace to <paramref name="server"/> with a token.</summary>
    public Run Connect(FakeZabbix server, string extra = "")
        => Invoke($"Connect-ZbxServer -Uri '{server.Endpoint}' -ApiToken (ConvertTo-SecureString 'test-token' -AsPlainText -Force) {extra}");

    public void Dispose()
    {
        // Bounded like Invoke: a runspace whose close waits on a pipeline that never ends fails the test, naming it.
        if (!Task.Run(Runspace.Dispose).Wait(CloseDeadline)) throw new TimeoutException($"the runspace did not close within {CloseDeadline}");
    }
}

/// <summary>
/// A script running in the background (<see cref="Shell.Start(string, IDictionary{string, object?}?)"/>). Disposing it
/// is bounded: a pipeline still running when its test ends (the test failed before it stopped the pipeline) is stopped
/// within <see cref="Shell.CloseDeadline"/>, and one that does not stop fails the test naming the script — <see cref="System.Management.Automation.PowerShell.Dispose()"/>
/// on a running pipeline stops it synchronously and without a bound, which can turn one failed stop test into a run that
/// never ends.
/// </summary>
internal sealed class Call : IAsyncDisposable
{
    private readonly System.Management.Automation.PowerShell _pipeline;
    private readonly TimeSpan _stopDeadline;

    public Call(System.Management.Automation.PowerShell pipeline, string script, TimeSpan stopDeadline)
    {
        _pipeline = pipeline;
        _stopDeadline = stopDeadline;
        Script = script;
        Running = pipeline.InvokeAsync();
    }

    public string Script { get; }

    /// <summary>The pipeline's output, once it ends; faulted with <see cref="PipelineStoppedException"/> when it was stopped.</summary>
    public Task<PSDataCollection<PSObject>> Running { get; }

    public PSDataStreams Streams => _pipeline.Streams;

    public PSInvocationStateInfo InvocationStateInfo => _pipeline.InvocationStateInfo;

    /// <summary>Ctrl+C: completes when the pipeline has stopped (the caller bounds the wait).</summary>
    public Task StopAsync() => _pipeline.StopAsync(null, null);

    public async ValueTask DisposeAsync()
    {
        if (!Running.IsCompleted)
        {
            _ = _pipeline.StopAsync(null, null);
            if (await Task.WhenAny(Running, Task.Delay(_stopDeadline)) != Running)
                throw new TimeoutException($"the script did not stop within {_stopDeadline} of its test's end: {Script}");
        }
        _pipeline.Dispose();
    }
}

/// <summary>
/// One of the three modules (docs/adr/0003-powershell-module-split.md): its name, which is its manifest's, and its
/// assembly. This project's output holds the three modules' files side by side (it references the three projects).
/// </summary>
internal sealed record ModuleUnderTest(string Name, Assembly Assembly)
{
    public static readonly ModuleUnderTest Zabbix = new("Community.Zabbix", typeof(ZabbixCmdlet).Assembly);

    public static readonly ModuleUnderTest Agent = new("Community.Zabbix.Agent", typeof(Commands.StartZbxActiveAgentCommand).Assembly);

    public static readonly ModuleUnderTest Proxy = new("Community.Zabbix.Proxy", typeof(Commands.StartZbxActiveProxyCommand).Assembly);

    public static IReadOnlyList<ModuleUnderTest> All { get; } = [Zabbix, Agent, Proxy];

    /// <summary>The module names, for a theory that runs once per module.</summary>
    public static TheoryData<string> Names => [.. All.Select(module => module.Name)];

    /// <summary>Every compiled cmdlet of the three modules.</summary>
    public static IEnumerable<Type> AllCmdlets => All.SelectMany(module => module.Cmdlets);

    /// <summary>The repository this test run was built from.</summary>
    public static string RepositoryRoot { get; } = Metadata("RepositoryRoot");

    /// <summary>The configuration this test run was built in.</summary>
    public static string Configuration { get; } = Metadata("Configuration");

    public static ModuleUnderTest Named(string name) => All.Single(module => module.Name == name);

    public string AssemblyName => Assembly.GetName().Name!;

    /// <summary>The manifest, beside the module assembly in this project's output.</summary>
    public string Manifest => Path.Combine(Path.GetDirectoryName(Assembly.Location)!, Name + ".psd1");

    /// <summary>The module's MAML help in this project's output.</summary>
    public string HelpFile => Path.Combine(Path.GetDirectoryName(Assembly.Location)!, "en-US", AssemblyName + ".dll-Help.xml");

    /// <summary>
    /// The module's project folder: the manifest template, the examples, the about_ topics. It is the assembly's name
    /// without <c>Community.</c> (<c>src/Zabbix.PowerShell</c> builds <c>Community.Zabbix.PowerShell</c>).
    /// </summary>
    public string ProjectDirectory => Path.Combine(RepositoryRoot, "src", AssemblyName["Community.".Length..]);

    /// <summary>The reference pages the module's build writes into the committed <c>docs/reference/&lt;module&gt;/</c>, which docfx publishes.</summary>
    public string ReferenceDirectory => Path.Combine(RepositoryRoot, "docs", "reference", Name);

    /// <summary>The module project's own build output: the module folder as it is built (before staging).</summary>
    public string BuildDirectory => Path.Combine(ProjectDirectory, "bin", Configuration, "net10.0");

    /// <summary>The module's compiled cmdlets.</summary>
    public IEnumerable<Type> Cmdlets => Assembly.GetExportedTypes().Where(type => !type.IsAbstract && type.GetCustomAttribute<CmdletAttribute>() is not null);

    public override string ToString() => Name;

    private static string Metadata(string key)
        => typeof(ModuleUnderTest).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;
}

internal sealed record Run(
    Collection<PSObject> Output,
    IReadOnlyList<ErrorRecord> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<InformationRecord> Information,
    IReadOnlyList<string> Debug,
    IReadOnlyList<string> Verbose,
    IReadOnlyList<ProgressRecord> Progress);

/// <summary>A <c>Remove-</c> cmdlet exactly as the generator emits it, for the host entity.</summary>
[Cmdlet(VerbsCommon.Remove, "ZbxTestHost", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
public sealed class RemoveZbxTestHostCommand : ZabbixRemoveCmdlet<ZabbixHost>
{
    /// <summary>The hosts.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [ZabbixReference<ZabbixHost>]
    [ZabbixCompleter<ZabbixHost>]
    public override ZabbixReference<ZabbixHost>[]? InputObject { get; set; }

    /// <inheritdoc/>
    protected override Task DeleteAsync(ZabbixClient client, IReadOnlyList<ulong> ids, CancellationToken cancellationToken)
        => client.Hosts.DeleteAsync(ids, cancellationToken);
}

/// <summary>
/// Exercises <see cref="ZabbixCmdlet.ShouldChange"/> directly (<c>ShouldProcess</c> + a <c>-Force</c>-gated
/// <c>ShouldContinue</c>): the shape kept for the handful of hand-written commands that batch many objects or
/// warrant a second question (Clear-ZbxHistory, an import whose rules delete what the document does not name); the
/// generated <c>New-</c>/<c>Set-</c>/<c>Remove-Zbx&lt;Entity&gt;</c> call <c>ShouldProcess</c> alone.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "ZbxTestShouldChange", SupportsShouldProcess = true)]
public sealed class InvokeZbxTestShouldChangeCommand : ZabbixCmdlet
{
    /// <summary>Skips the second confirmation (<c>ShouldContinue</c>); <c>-WhatIf</c>/<c>-Confirm</c> still apply.</summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    private bool _yesToAll;
    private bool _noToAll;

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (ShouldChange("test target", "Change", Force, ref _yesToAll, ref _noToAll)) WriteObject(true);
    }
}

/// <summary>Logs one record at every level through the connection's logger factory, inside an operation.</summary>
[Cmdlet(VerbsLifecycle.Invoke, "ZbxTestLog")]
public sealed partial class InvokeZbxTestLogCommand : ZabbixServerCmdlet
{
    /// <summary>After logging, throw the exception the Error record carried.</summary>
    [Parameter]
    public SwitchParameter Fail { get; set; }

    /// <summary>Log a resilience pipeline's Error-level attempt record (category <c>Polly</c>, another exception instance), then throw.</summary>
    [Parameter]
    public SwitchParameter Resilience { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (Resilience)
        {
            TryRun(_ =>
            {
                ILogger polly = ZabbixClient.Loggers.CreateLogger("Polly");
                AttemptLine(polly, new InvalidDataException("attempt 1 cause"));
                return Task.FromException(new InvalidDataException("the operation's own failure"));
            }, "probe");
            return;
        }
        TryRun(_ =>
        {
            ILogger log = ZabbixClient.Loggers.CreateLogger("Community.Zabbix.Tests.Probe");
            TraceLine(log);
            DebugLine(log);
            InformationLine(log);
            WarningLine(log);
            var failure = new InvalidDataException("error-line cause");
            ErrorLine(log, failure);
            return Fail ? Task.FromException(failure) : Task.CompletedTask;
        }, "probe");
    }

    [LoggerMessage(EventId = 9900, Level = LogLevel.Trace, Message = "trace-line")]
    private static partial void TraceLine(ILogger logger);

    [LoggerMessage(EventId = 9901, Level = LogLevel.Debug, Message = "debug-line")]
    private static partial void DebugLine(ILogger logger);

    [LoggerMessage(EventId = 9902, Level = LogLevel.Information, Message = "info-line")]
    private static partial void InformationLine(ILogger logger);

    [LoggerMessage(EventId = 9903, Level = LogLevel.Warning, Message = "warning-line")]
    private static partial void WarningLine(ILogger logger);

    [LoggerMessage(EventId = 9904, Level = LogLevel.Error, Message = "error-line")]
    private static partial void ErrorLine(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Execution attempt. Source: 'probe/Standard-Retry', Attempt: '1'")]
    private static partial void AttemptLine(ILogger logger, Exception exception);
}

/// <summary>Writes an endless stream through <c>WriteAll</c>; completes <see cref="Cancelled"/> when the read is cancelled.</summary>
[Cmdlet(VerbsLifecycle.Invoke, "ZbxTestStream")]
public sealed class InvokeZbxTestStreamCommand : ZabbixCmdlet
{
    /// <summary>Completed when the stream observes its cancellation.</summary>
    [Parameter(Mandatory = true)]
    public TaskCompletionSource? Cancelled { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord() => WriteAll(Forever(Cancelled!), "stream");

    private static async IAsyncEnumerable<int> Forever(TaskCompletionSource cancelled, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using CancellationTokenRegistration registration = cancellationToken.Register(() => cancelled.TrySetResult());
        for (int i = 0; ; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
            await Task.Yield();
        }
    }
}

/// <summary>Writes the trapper <see cref="ZabbixServerCmdlet.ResolveTrapperAsync"/> finds.</summary>
[Cmdlet(VerbsDiagnostic.Resolve, "ZbxTestTrapper")]
public sealed class ResolveZbxTestTrapperCommand : ZabbixServerCmdlet
{
    /// <summary>The trapper given on the command line.</summary>
    [Parameter]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "A cmdlet parameter that takes several values is an array: PowerShell's binder builds one.")]
    [ZabbixEndPoint]
    public System.Net.DnsEndPoint[]? Server { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (TryRun(cancellationToken => ResolveTrapperAsync(Server, cancellationToken), out IReadOnlyList<System.Net.DnsEndPoint>? nodes, Server)) WriteObject(nodes, enumerateCollection: true);
    }
}
