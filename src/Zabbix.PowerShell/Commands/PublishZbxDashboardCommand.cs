// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Creates or updates a dashboard from a document (<c>dashboard.create</c>/<c>dashboard.update</c>).</para>
/// <para type="description">The document is the JSON shape of <c>New-ZbxDashboard</c>'s request (name, pages, widgets).
/// A dashboard already named the same is updated in place; otherwise one is created.</para>
/// </summary>
[Cmdlet(VerbsData.Publish, "ZbxDashboard", SupportsShouldProcess = true, DefaultParameterSetName = ParameterSets.Path)]
[OutputType(typeof(ZabbixDashboard))]
public sealed class PublishZbxDashboardCommand : ZabbixServerCmdlet
{
    private static class ParameterSets
    {
        public const string Text = "Text";
        public const string Path = "Path";
        public const string LiteralPath = "LiteralPath";
    }

    /// <summary>
    /// The document: its JSON text, or the object itself (a hashtable, a <c>[pscustomobject]</c>, a
    /// <c>ZabbixDashboardCreateRequest</c>), read with the same field names as the JSON.
    /// </summary>
    /// <remarks>
    /// Bound through the request's own JSON contract (a piped hashtable would otherwise become the text
    /// "System.Collections.Hashtable").
    /// </remarks>
    [Parameter(ParameterSetName = ParameterSets.Text, Mandatory = true, ValueFromPipeline = true)]
    [ValidateNotNull]
    public object? Document { get; set; }

    /// <summary>
    /// Paths of document files; PowerShell wildcards (<c>*.json</c>) are expanded, and each file is its own document.
    /// </summary>
    [Parameter(ParameterSetName = ParameterSets.Path, Mandatory = true, Position = 0)]
    [SupportsWildcards]
    [ValidateNotNullOrEmpty]
    public string[]? Path { get; set; }

    /// <summary>Paths of document files, taken literally (no wildcards); a piped file (<c>Get-ChildItem *.json</c>) binds by its <c>PSPath</c>.</summary>
    [Parameter(ParameterSetName = ParameterSets.LiteralPath, Mandatory = true, ValueFromPipelineByPropertyName = true)]
    [Alias("PSPath", "LP")]
    [ValidateNotNullOrEmpty]
    public string[]? LiteralPath { get; set; }

    /// <summary>Writes the published dashboard back, instead of nothing.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (ParameterSetName == ParameterSets.Text)
        {
            Publish(ZabbixJsonBinder.Unwrap(Document)!, Document!);
            return;
        }
        foreach ((string file, string text, ErrorRecord? unreadable) in ZabbixDocumentFile.ReadAll(this, Path ?? LiteralPath!, literal: Path is null))
        {
            if (unreadable is not null) WriteError(unreadable);
            else Publish(text, file);
        }
    }

    // One document, created or updated by its name.
    private void Publish(object document, object target)
    {
        // The template form's widget shorthands (graph = '<name>', item = @{ host; key }) are
        // resolved to ids first — reads only, so before ShouldProcess, which needs the bound request's name.
        if (document is IDictionary template && ZabbixDashboardShorthand.Uses(template))
        {
            if (!TryRun(cancellationToken => ZabbixDashboardShorthand.ResolveAsync(template, ZabbixClient, cancellationToken), out IDictionary? resolved, target)) return;
            document = resolved;
        }
        ZabbixDashboardCreateRequest? request;
        try
        {
            request = document is string json
                ? JsonSerializer.Deserialize(json, ZabbixContracts.Of<ZabbixDashboardCreateRequest>())
                : ZabbixJsonBinder.Bind(document, ZabbixContracts.Of<ZabbixDashboardCreateRequest>());
        }
        catch (Exception failure) when (failure is not PipelineStoppedException)
        {
            WriteError(new ErrorRecord(failure, "ZabbixDashboardDocumentInvalid", ErrorCategory.InvalidData, target));
            return;
        }
        if (request is null)
        {
            WriteError(new ErrorRecord(new PSArgumentException("the document did not deserialize to a dashboard (an empty or 'null' document)"),
                "ZabbixDashboardDocumentInvalid", ErrorCategory.InvalidData, target));
            return;
        }
        if (!ShouldProcess(request.Name, "Publish dashboard")) return;

        if (!TryRun(async cancellationToken =>
        {
            var byName = new ZabbixDashboardGetParameters { Filter = new() { ["name"] = request.Name } };
            IReadOnlyList<ZabbixDashboard> existing = await ListAsync(ZabbixClient.Dashboards.GetAsync(byName, cancellationToken), cancellationToken).ConfigureAwait(false);
            ulong id;
            if (existing.Count > 0)
            {
                var update = new ZabbixDashboardUpdateRequest
                {
                    DashboardId = existing[0].DashboardId!.Value,
                    Name = request.Name,
                    UserId = request.UserId,
                    Private = request.Private,
                    Users = request.Users,
                    UserGroups = request.UserGroups,
                    DisplayPeriod = request.DisplayPeriod,
                    AutoStart = request.AutoStart,
                    // dashboard.update's pages are their own contract (a page may name dashboard_pageid there, never on
                    // create: CDashboard.php 8.0:258 vs 317), so each create page is re-read through it
                    Pages = request.Pages is null ? null : [.. request.Pages.Select(page => ZabbixJsonBinder.Bind(page, ZabbixContracts.Of<ZabbixDashboardUpdatePageRequest>()))],
                };
                id = await ZabbixClient.Dashboards.UpdateAsync(update, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                id = await ZabbixClient.Dashboards.CreateAsync(request, cancellationToken).ConfigureAwait(false);
            }
            if (!PassThru.IsPresent) return null;
            IReadOnlyList<ZabbixDashboard> published = await ZabbixClient.Dashboards.GetAsync(new ZabbixDashboardGetParameters { DashboardIds = [id] }, cancellationToken).ConfigureAwait(false);
            return published.Count > 0 ? published[0] : null;
        }, out ZabbixDashboard? dashboard, request.Name)) return;
        if (dashboard is not null) WriteObject(dashboard);
    }
}
