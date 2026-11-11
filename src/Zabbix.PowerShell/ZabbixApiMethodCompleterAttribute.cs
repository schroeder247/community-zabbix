// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Language;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Completes an API method name (<c>Invoke-ZbxApi -Method</c>) from the methods the model describes
/// (<see cref="ZabbixApiMethods.All"/>): those the connected server's version has, when the command's connection (its
/// <c>-Connection</c>, else the current one) knows its version, and every described method otherwise. No call is made.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixApiMethodCompleterAttribute : ArgumentCompleterFactoryAttribute
{
    /// <inheritdoc/>
    public override IArgumentCompleter Create() => new Completer();

    /// <summary>The method names that complete <paramref name="word"/> against a server of <paramref name="version"/> (any version when <see langword="null"/>), in order.</summary>
    /// <param name="word">What is typed so far.</param>
    /// <param name="version">The server's API version, when known.</param>
    internal static IEnumerable<string> Methods(string? word, Version? version)
        => ZabbixApiMethods.All.Keys
            .Where(method => method.StartsWith(word ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            .Where(method => version is null || ZabbixApiMethods.Supports(method, version))
            .Order(StringComparer.Ordinal);

    private sealed class Completer : IArgumentCompleter
    {
        public IEnumerable<CompletionResult> CompleteArgument(string commandName, string parameterName, string wordToComplete, CommandAst commandAst, IDictionary fakeBoundParameters)
        {
            string word = (wordToComplete ?? string.Empty).Trim('\'', '"');
            return Methods(word, Connection(fakeBoundParameters)?.ApiVersion)
                .Select(method => new CompletionResult(method, method, CompletionResultType.ParameterValue, method));
        }

        // The connection the command would use: -Connection by name or object, else the current one.
        private static ZabbixConnection? Connection(IDictionary fakeBoundParameters)
        {
            if (!ZabbixContext.TryGetCurrent(out ZabbixContext? context)) return null;
            object? given = fakeBoundParameters?["Connection"];
            given = given is PSObject wrapped ? wrapped.BaseObject : given;
            return given switch
            {
                ZabbixConnection connection => connection,
                string name when context.TryGet(name, out ZabbixConnection? named) => named,
                _ => context.Current,
            };
        }
    }
}
