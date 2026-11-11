// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Reflection;
using Community.Zabbix.Tests.Conformance;
using Microsoft.Extensions.Compliance.Classification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// A classified log parameter (<c>[ZabbixSecret]</c>, <c>[ZabbixPersonal]</c>, <c>[ZabbixItemKeyData]</c>) is honoured
/// only when its assembly is compiled by the extended <c>[LoggerMessage]</c> generator of
/// <c>Microsoft.Extensions.Telemetry.Abstractions</c>. The standard generator in the shared framework compiles the same
/// declaration without a diagnostic and ignores the attribute, so a library that does not reference the package logs the
/// value in clear text. These rules hold every library to the behaviour, not the reference: each classified event is
/// invoked through an unredacted logger and must keep the value out of the message and the state.
/// </summary>
public sealed partial class DesignConformanceTests
{
    private const string ClassifiedSentinel = "classified-c1a55e7";

    /// <summary>The package whose generator honours a data classification on a log parameter.</summary>
    private const string ExtendedLoggingGenerator = "Microsoft.Extensions.Telemetry.Abstractions";

    [Fact]
    public void Every_classified_log_parameter_stays_out_of_an_unredacted_log()
    {
        List<MethodInfo> classified = [.. LoggerMessages().Select(entry => entry.Method).Where(HasClassifiedParameter)];

        Assert.NotEmpty(classified);
        AssertNone(ClassifiedLeaks(classified));
    }

    /// <summary>
    /// A redactor erases a classified value, so a template that ends at a classified placeholder ends mid-sentence
    /// ("could not be asked for "). Each one either names what the value is and holds its place (quoted), or ends the
    /// sentence before it and appends the value as a detail after a colon, so the message stays a sentence when the
    /// value is gone.
    /// </summary>
    [Fact]
    public void No_log_template_ends_at_a_classified_placeholder()
    {
        static bool EndsAt(string message, string placeholder)
        {
            string trimmed = message.TrimEnd('.', ' ');
            return trimmed.EndsWith(placeholder, StringComparison.OrdinalIgnoreCase)
                && !trimmed[..^placeholder.Length].Contains(": ", StringComparison.Ordinal);
        }

        List<string> offenders = [.. LoggerMessages()
            .Where(entry => entry.Method.GetParameters().Any(parameter => parameter.GetCustomAttributes().OfType<DataClassificationAttribute>().Any()
                && EndsAt(entry.Attribute.Message, "{" + parameter.Name + "}")))
            .Select(entry => $"{entry.Attribute.EventId} {entry.Method.Name}: \"{entry.Attribute.Message}\"")];

        AssertNone(offenders);
    }

    [Fact]
    public void Every_library_with_a_classified_log_parameter_is_built_by_the_extended_generator()
    {
        List<string> offenders = [.. LoggerMessages().Select(entry => entry.Method).Where(HasClassifiedParameter)
            .Select(method => method.DeclaringType!.Assembly).Distinct()
            .Where(assembly => !assembly.GetReferencedAssemblies().Any(reference => reference.Name == ExtendedLoggingGenerator))
            .Select(assembly => $"{assembly.GetName().Name} declares a classified log parameter and does not reference {ExtendedLoggingGenerator}")];

        Assert.Contains(typeof(ZabbixGetter).Assembly, Libraries.All);
        AssertNone(offenders);
    }

    [Fact]
    public void The_leak_rule_finds_a_classified_value_in_a_log()
        => Assert.Equal(
            [$"{typeof(ClassifiedLeakProbe).FullName}.{nameof(ClassifiedLeakProbe.Leak)}: key"],
            ClassifiedLeaks([typeof(ClassifiedLeakProbe).GetMethod(nameof(ClassifiedLeakProbe.Leak))!]));

    private static bool HasClassifiedParameter(MethodInfo method)
        => method.GetParameters().Any(parameter => parameter.GetCustomAttributes<DataClassificationAttribute>().Any());

    /// <summary>
    /// Invokes each method with a sentinel in every classified parameter and returns <c>Type.Method: parameter</c> for each
    /// sentinel that reached the message or the state of the record an unredacted <see cref="FakeLogger"/> collected.
    /// </summary>
    internal static IEnumerable<string> ClassifiedLeaks(IEnumerable<MethodInfo> methods)
    {
        foreach (MethodInfo method in methods)
        {
            var collector = new FakeLogCollector();
            ParameterInfo[] parameters = method.GetParameters();
            object?[] arguments = [.. parameters.Select(parameter => SampleArgument(parameter, new FakeLogger(collector)))];
            method.Invoke(null, arguments);
            FakeLogRecord record = collector.LatestRecord;
            IEnumerable<string?> rendered = (record.StructuredState?.Select(pair => pair.Value) ?? []).Append(record.Message);
            foreach (ParameterInfo parameter in parameters.Where(parameter => parameter.GetCustomAttributes<DataClassificationAttribute>().Any()))
            {
                string sentinel = Sentinel(parameter);
                if (rendered.Any(text => text?.Contains(sentinel, StringComparison.Ordinal) == true))
                    yield return $"{method.DeclaringType!.FullName}.{method.Name}: {parameter.Name}";
            }
        }
    }

    private static string Sentinel(ParameterInfo parameter) => $"{ClassifiedSentinel}-{parameter.Position}";

    /// <summary>A value of the parameter's type: a sentinel where it is classified, an ordinary value otherwise.</summary>
    private static object? SampleArgument(ParameterInfo parameter, ILogger logger)
    {
        Type type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        bool classified = parameter.GetCustomAttributes<DataClassificationAttribute>().Any();
        string text = classified ? Sentinel(parameter) : "value";
        return type switch
        {
            _ when type == typeof(ILogger) => logger,
            _ when type == typeof(string) => text,
            _ when type == typeof(ZabbixItemKey) => ZabbixItemKey.Parse($"web.page.get[{text}]"),
            _ when type == typeof(IPAddress) => IPAddress.Loopback,
            _ when type == typeof(ZabbixServerNode) => ZabbixServerNode.Parse("127.0.0.1:10051", null),
            _ when typeof(Exception).IsAssignableFrom(type) => new InvalidOperationException("The sample failure."),
            _ when classified => throw new InvalidOperationException($"{parameter.Member.Name}.{parameter.Name} is classified and of type {type.Name}, which the leak rule cannot fill with a sentinel."),
            _ when type.IsValueType => Activator.CreateInstance(type),
            _ => null,
        };
    }
}

/// <summary>A hand-written log call that renders a classified value: the leak rule must find it.</summary>
internal static class ClassifiedLeakProbe
{
    public static void Leak(ILogger logger, [ZabbixItemKeyData] string key)
        => logger.Log(LogLevel.Warning, new EventId(1), new List<KeyValuePair<string, object?>> { new("Key", key) }, null, static (state, _) => $"Key {state[0].Value}");
}
