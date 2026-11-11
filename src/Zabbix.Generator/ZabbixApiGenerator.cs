// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.CodeAnalysis;

namespace Community.Zabbix.Generator;

/// <summary>
/// Generates the API from its description. Every enum, record, parameter set and request in <c>zabbix-api.json</c>
/// (extracted from the Zabbix source by <c>tools/ZabbixApiExtract</c>) is emitted as a type; every API object as a client
/// with the methods it has; the clients as the properties of <c>ZabbixApi</c>; the methods' capabilities as
/// <c>ZabbixApiMethods</c>. Nothing about an entity is written twice, and nothing is reflected over at run time.
/// What the emitter cannot type is a diagnostic at the description's line, never a silent <c>JsonElement</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ZabbixApiGenerator : IIncrementalGenerator
{
    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // A generator's output is invisible to the other generators, so the records go to one assembly
        // (ZabbixEmit=Models) and the clients that System.Text.Json's context must serialize them for go to another
        // (ZabbixEmit=Clients); the context itself is written before CoreCompile by the build step in Zabbix.Sdk.csproj,
        // from this same emitter.
        IncrementalValuesProvider<GeneratorOutput> output = DescriptionPipeline.Select(context, static name => name == "zabbix-api.json")
            .Select(static (selected, cancellationToken) => DescriptionPipeline.Parse(selected.Text, selected.Part == "Clients" ? "Clients" : "Models", cancellationToken))
            .WithTrackingName(DescriptionPipeline.ParseStep)
            .Select(static (parsed, cancellationToken) => DescriptionPipeline.Emit(
                parsed,
                static (api, part, token) => new ZabbixApiEmitter(api).Emit(part == "Clients" ? ZabbixApiPart.Clients : ZabbixApiPart.Models, token),
                cancellationToken))
            .WithTrackingName(DescriptionPipeline.EmitStep);

        context.RegisterSourceOutput(output, DescriptionPipeline.Produce);
    }
}
