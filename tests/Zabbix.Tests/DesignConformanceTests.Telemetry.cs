// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Reflection.Emit;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Tests.Conformance;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Tests;

/// <summary>
/// The telemetry every layer shares through <see cref="ZabbixLayerTelemetry"/>.
/// Each rule reads what is actually there (the IL a layer's constructor emits, the shared Start/Finish contract, the test
/// sources' assertions) rather than a hand-kept list of layers or event ids.
/// </summary>
public sealed partial class DesignConformanceTests
{
    /// <summary>
    /// Every name an application subscribes to falls under <see cref="ZabbixTelemetryNames.Prefix"/>
    /// (<c>Zabbix.*</c> matches it). First, the catalogue itself: every layer constant <see cref="ZabbixTelemetryNames"/>
    /// declares (everything but <see cref="ZabbixTelemetryNames.Prefix"/> and <see cref="ZabbixTelemetryNames.Subscription"/>,
    /// found by reflection, not named) starts with it. Second, every concrete <see cref="ZabbixLayerTelemetry"/> in the
    /// libraries actually uses one of those catalogued values as a literal somewhere in its own IL — not a hand-typed
    /// string that merely happens to look right — found by comparing against the fields' own runtime values, never a copy
    /// of them kept here.
    /// </summary>
    [Fact]
    public void Telemetry_names_share_the_root_prefix()
    {
        List<string> layerNames = [.. typeof(ZabbixTelemetryNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(value => value != ZabbixTelemetryNames.Prefix && value != ZabbixTelemetryNames.Subscription)];
        Assert.NotEmpty(layerNames);
        AssertNone(layerNames.Where(name => !name.StartsWith(ZabbixTelemetryNames.Prefix, StringComparison.Ordinal))
            .Select(name => $"{name} does not start with {ZabbixTelemetryNames.Prefix}"));

        List<Type> telemetryTypes = [.. Libraries.Types.Where(type => !type.IsAbstract && typeof(ZabbixLayerTelemetry).IsAssignableFrom(type))];
        Assert.NotEmpty(telemetryTypes);
        List<string> offenders = [.. telemetryTypes
            .Where(type => !layerNames.Any(name => CallGraph.Of.Methods
                .Where(method => method.DeclaringType == type)
                .SelectMany(method => CallGraph.Of.Instructions(method))
                .Any(instruction => instruction.Operand as string == name)))
            .Select(type => type.FullName!)];
        AssertNone(offenders);
    }

    /// <summary>
    /// One prefix per layer: every <c>zabbix.</c> span, instrument, event and tag name the API, tree, wire, listener and
    /// store layers declare (the trapper's under the listener's) starts with the layer's own <c>zabbix.{layer}.</c>, so a dashboard filters a layer by one prefix. The TLS
    /// attributes (<c>zabbix.tls.*</c>) are shared by every layer that speaks TLS. Read from the IL too, so a literal
    /// the telemetry type writes beside its constants is held to the same rule.
    /// </summary>
    [Theory]
    [InlineData(typeof(ZabbixApiTelemetry), "zabbix.api.")]
    [InlineData(typeof(ZabbixTreeTelemetry), "zabbix.tree.")]
    [InlineData(typeof(Zabbix.Protocol.ZabbixWireTelemetry), "zabbix.wire.")]
    [InlineData(typeof(Zabbix.Listening.ZabbixListenerTelemetry), "zabbix.listener.")]
    [InlineData(typeof(Zabbix.Listening.ZabbixTrapper), "zabbix.listener.")]
    [InlineData(typeof(Zabbix.Storage.ZabbixStoreTelemetry), "zabbix.store.")]
    [InlineData(typeof(Zabbix.Hosting.ZabbixAgentTelemetry), "zabbix.agent.")]
    [InlineData(typeof(Zabbix.Proxy.ZabbixProxyTelemetry), "zabbix.proxy.")]
    public void Every_name_a_client_layer_writes_starts_with_its_layer_prefix(Type layer, string prefix)
    {
        List<string> names = [.. layer.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Concat(CallGraph.Of.Methods.Where(method => method.DeclaringType == layer).SelectMany(method => CallGraph.Of.Instructions(method)).Select(instruction => instruction.Operand as string).OfType<string>())
            .Where(name => name.StartsWith("zabbix.", StringComparison.Ordinal) && !name.StartsWith("zabbix.tls.", StringComparison.Ordinal))];

        Assert.NotEmpty(names);
        AssertNone(names.Where(name => !name.StartsWith(prefix, StringComparison.Ordinal)).Select(name => $"{layer.Name} writes {name}, not under {prefix}"));
    }

    /// <summary>
    /// A duration is always seconds, always a <see cref="double"/> (the OpenTelemetry convention every other
    /// histogram in the process follows, so two SDKs' durations are comparable without a conversion). Read from the IL:
    /// every call to <see cref="Meter.CreateHistogram{T}(string, string?, string?, IEnumerable{KeyValuePair{string, object}}?)"/>
    /// in the libraries names <see cref="double"/> as <c>T</c> and carries the literal unit <c>"s"</c> nearby (the only
    /// histogram the libraries declare is the one shared base builds — <see cref="ZabbixLayerTelemetry"/> — so this also
    /// proves nothing has grown its own, differently-united, duration instrument beside it).
    /// </summary>
    [Fact]
    public void Every_duration_instrument_is_seconds_double()
    {
        List<string> offenders = [];
        foreach (MethodBase method in CallGraph.Of.Methods)
        {
            List<(OpCode Op, object Operand)> body = [.. CallGraph.Of.Instructions(method)];
            for (int i = 0; i < body.Count; i++)
            {
                if (body[i].Operand is not MethodInfo { Name: "CreateHistogram", IsGenericMethod: true } createHistogram || createHistogram.DeclaringType != typeof(Meter)) continue;
                Type measured = createHistogram.GetGenericArguments()[0];
                if (measured != typeof(double)) offenders.Add($"{Describe(method)}: CreateHistogram<{measured.Name}>");
                List<string> nearby = [.. body.Skip(Math.Max(0, i - 8)).Take(8).Select(entry => entry.Operand as string).OfType<string>()];
                if (!nearby.Contains("s")) offenders.Add($"{Describe(method)}: no literal \"s\" unit found beside the CreateHistogram<{measured.Name}> call ({string.Join(", ", nearby)})");
            }
        }
        AssertNone(offenders);
    }

    /// <summary>
    /// A duration in seconds carries bucket advice: without <see cref="InstrumentAdvice{T}.HistogramBucketBoundaries"/>, an
    /// OpenTelemetry SDK records it into its default boundaries, which are made for milliseconds, so every duration under
    /// five seconds lands in one bucket and no percentile can be read. Read from the IL: every call to
    /// <c>Meter.CreateHistogram</c> in the libraries is the overload that takes an <see cref="InstrumentAdvice{T}"/>.
    /// </summary>
    [Fact]
    public void Every_duration_instrument_carries_bucket_advice()
    {
        List<string> offenders = [];
        foreach (MethodBase method in CallGraph.Of.Methods)
        {
            foreach ((OpCode _, object operand) in CallGraph.Of.Instructions(method))
            {
                if (operand is not MethodInfo { Name: "CreateHistogram", IsGenericMethod: true } createHistogram || createHistogram.DeclaringType != typeof(Meter)) continue;
                if (!createHistogram.GetParameters().Any(parameter => parameter.ParameterType.IsGenericType && parameter.ParameterType.GetGenericTypeDefinition() == typeof(InstrumentAdvice<>)))
                    offenders.Add($"{Describe(method)}: CreateHistogram without bucket advice");
            }
        }
        AssertNone(Waivers.Outstanding(nameof(Every_duration_instrument_carries_bucket_advice), offenders));
    }

    [Fact]
    public void The_shared_duration_advice_has_seconds_boundaries_up_to_a_minute()
    {
        IReadOnlyList<double> boundaries = ZabbixLayerTelemetry.DurationAdvice.HistogramBucketBoundaries!;

        Assert.Equal(0.005, boundaries[0]);
        Assert.Equal(60, boundaries[^1]);
        Assert.Equal(boundaries.Order(), boundaries);
    }

    /// <summary>
    /// A span that measures an exchange with the server is never left open. Read structurally, not by a method
    /// name that varies per layer (<c>StartExchange</c>, <c>Start</c>, <c>StartCommand</c>): a method that returns
    /// <see cref="ZabbixOperation"/> opens one; a method that takes a <see cref="ZabbixOperation"/> parameter closes one
    /// (<see cref="ZabbixLayerTelemetry.Finish"/> and every layer's own overload of it). A method whose own return type is
    /// <see cref="ZabbixOperation"/> is itself just such an opener (a layer's <c>Start*</c> helper: it hands the operation
    /// back to its caller, who owns closing it) and is exempt; every other library method that directly calls an opener
    /// also directly calls a closer — including a compiler's <c>MoveNext</c>, which is where an <c>async</c> method's own
    /// calls actually live, found here as an ordinary declared method like any other.
    /// </summary>
    [Fact]
    public void Every_exchange_boundary_starts_a_span()
    {
        bool Opens(MethodBase callee) => callee is MethodInfo { ReturnType: var returns } && returns == typeof(ZabbixOperation) && Libraries.Contains(callee.Module.Assembly);
        bool Closes(MethodBase callee) => Libraries.Contains(callee.Module.Assembly)
            && callee.GetParameters().Any(parameter => parameter.ParameterType == typeof(ZabbixOperation)
                || (parameter.ParameterType.IsByRef && parameter.ParameterType.GetElementType() == typeof(ZabbixOperation)));

        List<MethodBase> boundaries = [.. CallGraph.Of.Methods
            .Where(method => !(method is MethodInfo { ReturnType: var returns } && returns == typeof(ZabbixOperation)))
            .Where(method => CallGraph.Of.Instructions(method).Any(instruction => instruction.Operand is MethodBase callee && Opens(callee)))];
        Assert.NotEmpty(boundaries);

        List<string> offenders = [.. boundaries
            .Where(method => !CallGraph.Of.Instructions(method).Any(instruction => instruction.Operand is MethodBase callee && Closes(callee)))
            .Select(Describe)];
        AssertNone(offenders);
    }

    /// <summary>
    /// A failed exchange's span and its duration sample always carry <c>error.type</c>, and always the same one
    /// Every layer shares the one place this is written, <see cref="ZabbixLayerTelemetry.Finish"/>,
    /// proven directly on it, on a fake source and meter, rather than re-checked per layer (a per-layer check would only
    /// prove that layer calls the shared method, which <see cref="Every_exchange_boundary_starts_a_span"/> already does).
    /// </summary>
    [Fact]
    public void Every_failed_span_and_duration_carries_error_type()
    {
        var activities = new ConcurrentBag<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetryProbe.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(activity),
        };
        ActivitySource.AddActivityListener(activityListener);

        var samples = new ConcurrentBag<KeyValuePair<string, object?>[]>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == TelemetryProbe.MeterName) listener.EnableMeasurementEvents(instrument);
            },
        };
        meterListener.SetMeasurementEventCallback<double>((_, _, tags, _) => samples.Add([.. tags]));
        meterListener.Start();

        using var telemetry = new TelemetryProbe();
        var failure = new InvalidOperationException("boom");
        ZabbixOperation operation = telemetry.Start("probe", ActivityKind.Internal, default);
        telemetry.Finish(operation, failure);

        Activity span = Assert.Single(activities);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem("error.type"));
        KeyValuePair<string, object?>[] sample = Assert.Single(samples);
        Assert.Contains(sample, tag => tag.Key == "error.type" && (string?)tag.Value == typeof(InvalidOperationException).FullName);
    }

    /// <summary>
    /// Every event a layer logs at Warning or above is asserted by a test (<c>FakeLogCollector</c> or the
    /// equivalent), so a silent failure path cannot ship. "Asserted" is read structurally from the test sources, not from
    /// a hand-kept list of covered ids: an equality or an <c>is</c>-pattern whose other side is a member access chain
    /// ending in <c>.Id</c> (<c>record.Id.Id == 1103</c>, <c>entry.Id == 3206</c>, <c>entry.Id is 3203 or 3104</c>) marks
    /// that integer as covered. A rule with no test names an offender; a known one is
    /// recorded in <see cref="Waivers"/> exactly like every other rule here — never a silent exemption.
    /// </summary>
    [Fact]
    public void Every_warning_or_error_event_is_asserted_by_a_test()
    {
        HashSet<int> asserted = AssertedEventIds();
        Assert.NotEmpty(asserted);

        List<string> offenders = [.. LoggerMessages()
            .Where(entry => entry.Attribute.Level >= LogLevel.Warning)
            .Where(entry => !asserted.Contains(entry.Attribute.EventId))
            .Select(entry => $"{entry.Attribute.EventId} {Describe(entry.Method)}")];
        AssertNone(Waivers.Outstanding(nameof(Every_warning_or_error_event_is_asserted_by_a_test), offenders));
    }

    /// <summary>The integers the test sources compare a <c>.Id</c> member access chain against, by <c>==</c> or an <c>is</c> pattern.</summary>
    private static HashSet<int> AssertedEventIds()
    {
        var ids = new HashSet<int>();
        foreach (SyntaxTree tree in Sources.Tests.SyntaxTrees)
        {
            foreach (BinaryExpressionSyntax binary in tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>())
            {
                if (binary.Kind() != SyntaxKind.EqualsExpression) continue;
                (ExpressionSyntax member, ExpressionSyntax literal) = binary.Left is LiteralExpressionSyntax ? (binary.Right, binary.Left) : (binary.Left, binary.Right);
                if (literal is LiteralExpressionSyntax { Token.Value: int id } && EndsInId(member)) ids.Add(id);
            }
            foreach (IsPatternExpressionSyntax isPattern in tree.GetRoot().DescendantNodes().OfType<IsPatternExpressionSyntax>())
            {
                if (!EndsInId(isPattern.Expression)) continue;
                foreach (int id in PatternIds(isPattern.Pattern)) ids.Add(id);
            }
        }
        return ids;
    }

    private static bool EndsInId(ExpressionSyntax expression) => expression is MemberAccessExpressionSyntax member && member.Name.Identifier.Text == "Id";

    private static IEnumerable<int> PatternIds(PatternSyntax pattern)
    {
        switch (pattern)
        {
            case ConstantPatternSyntax { Expression: LiteralExpressionSyntax { Token.Value: int id } }: yield return id; break;
            case BinaryPatternSyntax { RawKind: (int)SyntaxKind.OrPattern } either:
                foreach (int id in PatternIds(either.Left)) yield return id;
                foreach (int id in PatternIds(either.Right)) yield return id;
                break;
            case ParenthesizedPatternSyntax inner:
                foreach (int id in PatternIds(inner.Pattern)) yield return id;
                break;
        }
    }
}

/// <summary>
/// A minimal, concrete <see cref="ZabbixLayerTelemetry"/> that proves the shared Start/Finish contract directly
/// (<see cref="DesignConformanceTests.Every_failed_span_and_duration_carries_error_type"/>): the base does the actual
/// work, so testing this probe tests every layer that derives from it.
/// </summary>
internal sealed class TelemetryProbe : ZabbixLayerTelemetry
{
    public const string SourceName = "Zabbix.ConformanceProbe";
    public const string MeterName = "Zabbix.ConformanceProbe";
    private const string DurationInstrument = "zabbix.conformance_probe.duration";

    private static readonly ActivitySource Probe = new(SourceName);

    public TelemetryProbe() : base(Probe, MeterName, DurationInstrument, "A conformance probe's duration.", meters: null, time: null)
    {
    }
}
