// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.CodeAnalysis;

namespace Community.Zabbix.Generator;

/// <summary>
/// Generates the PowerShell module's CRUD cmdlets from the API description. Consumed by
/// <c>Zabbix.PowerShell.csproj</c> with the description copied as <c>zabbix-api.cmdlets.json</c> and
/// <c>ZabbixEmit="Cmdlets"</c> (a file literally named <c>zabbix-api.json</c> would also start <see cref="ZabbixApiGenerator"/>),
/// beside <see cref="ZabbixApiGenerator"/> (Models/Clients), never changing what it emits.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ZabbixCmdletGenerator : IIncrementalGenerator
{
    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<GeneratorOutput> output = DescriptionPipeline.Select(context, static name => name.StartsWith("zabbix-api", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            .Where(static selected => selected.Part == "Cmdlets")
            .Select(static (selected, cancellationToken) => DescriptionPipeline.Parse(selected.Text, "Cmdlets", cancellationToken))
            .WithTrackingName(DescriptionPipeline.ParseStep)
            .Select(static (parsed, cancellationToken) => DescriptionPipeline.Emit(parsed, static (api, _, token) => new ZabbixCmdletEmitter(api).Emit(token), cancellationToken))
            .WithTrackingName(DescriptionPipeline.EmitStep);

        context.RegisterSourceOutput(output, DescriptionPipeline.Produce);
    }
}
