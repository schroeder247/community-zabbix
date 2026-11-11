// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Language;

namespace Community.Zabbix.PowerShell;

/// <summary>Completes <c>-Connection</c> with the names in the runspace's context.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixConnectionCompleterAttribute : ArgumentCompleterFactoryAttribute
{
    /// <inheritdoc/>
    public override IArgumentCompleter Create() => new Completer();

    private sealed class Completer : IArgumentCompleter
    {
        public IEnumerable<CompletionResult> CompleteArgument(string commandName, string parameterName, string wordToComplete, CommandAst commandAst, IDictionary fakeBoundParameters)
            => ZabbixContext.TryGetCurrent(out ZabbixContext? context)
                ? context.Servers.Keys
                    .Where(name => name.StartsWith(wordToComplete ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Select(name => new CompletionResult(ZabbixCompletion.Quote(name), name, CompletionResultType.ParameterValue, name))
                : [];
    }
}
