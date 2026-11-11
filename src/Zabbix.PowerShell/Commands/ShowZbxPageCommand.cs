// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Opens an object's page in the Zabbix web frontend.</para>
/// <para type="description">Opens the frontend page of each object piped in (any record from a Get-Zbx command:
/// hosts, host and template groups, templates, items, triggers, graphs, discovery rules, web scenarios, dashboards,
/// maps, problems, events, maintenances, users, user groups, proxies, network discovery rules, actions, media types,
/// scripts, services, SLAs), or, with -Kind, of the objects whose ids are piped or given (-Id), in the default
/// browser. The page is the one the server's own frontend version links the object by; -View picks another of its
/// pages (Latest data, Problems, an item's history). The frontend is the API endpoint's folder unless
/// Connect-ZbxServer -FrontendUri named another. -PassThru writes the address instead of opening it. Where no browser can be opened (an SSH
/// session, a CI runner), the command warns and writes the address. A record read without what the page needs (an
/// item without its host, a bare id) is read again by id first.</para>
/// </summary>
[Cmdlet(VerbsCommon.Show, "ZbxPage")]
[OutputType(typeof(Uri))]
public sealed class ShowZbxPageCommand : ZabbixServerCmdlet
{
    private static readonly ZabbixOutput<ZabbixHost> s_hostsAndStatus = [ZabbixHostFields.HostId, ZabbixHostFields.Status];
    private static readonly ZabbixOutput<ZabbixItem> s_itemFields = [ZabbixItemFields.ItemId, ZabbixItemFields.HostId, ZabbixItemFields.Name];
    private static readonly ZabbixOutput<ZabbixDiscoveryRule> s_discoveryRuleFields = [ZabbixDiscoveryRuleFields.ItemId, ZabbixDiscoveryRuleFields.HostId, ZabbixDiscoveryRuleFields.Name];
    private static readonly ZabbixOutput<ZabbixTrigger> s_triggerFields = [ZabbixTriggerFields.TriggerId, ZabbixTriggerFields.Description];
    private static readonly ZabbixOutput<ZabbixGraph> s_graphFields = [ZabbixGraphFields.GraphId, ZabbixGraphFields.Name];
    private static readonly ZabbixOutput<ZabbixHttpTest> s_webScenarioFields = [ZabbixHttpTestFields.HttpTestId, ZabbixHttpTestFields.HostId, ZabbixHttpTestFields.Name];

    private ZabbixFrontend? _frontend;

    /// <summary>
    /// The objects: any record a Get-Zbx command writes for an object the frontend has a page for; with -Kind, the
    /// objects' ids (-Id is the same parameter).
    /// </summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("Id")]
    [ValidateNotNull]
    public PSObject? InputObject { get; set; }

    /// <summary>Reads the input as ids of this kind of object, each read from the server for what its page needs.</summary>
    [Parameter]
    public ZabbixPageKind? Kind { get; set; }

    /// <summary>Which page: Default (the one the frontend opens the object on), Edit, Latest, Problems, Graph or Values, where the kind has it.</summary>
    [Parameter]
    public ZabbixPageView View { get; set; }

    /// <summary>Writes each page's address instead of opening it.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (_frontend is null && !TryRun(ZabbixClient.GetFrontendAsync, out _frontend, Connected.Name)) return;
        object value = InputObject!.BaseObject;
        if (Kind is { } kind)
        {
            if (IdOf(value) is { } id)
            {
                Show(_frontend, null, kind, id);
            }
            else
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"-Kind {kind} reads the input as ids, and '{value}' is not one"),
                    "ZabbixPageNotAnId", ErrorCategory.InvalidArgument, value));
            }
        }
        else if (ZabbixPageTarget.TryFrom(value, out ZabbixPageTarget? target))
        {
            Show(_frontend, target, target.Kind, target.Id);
        }
        else
        {
            WriteError(new ErrorRecord(
                new ArgumentException($"a {value.GetType().Name} has no page in the frontend: pipe a record from a Get-Zbx command, or give -Kind with ids"),
                "ZabbixPageUnknown", ErrorCategory.InvalidArgument, value));
        }
    }

    // An id as Get-'s -Id takes one: a ulong, a decimal string, a non-negative integer.
    private static ulong? IdOf(object value) => value switch
    {
        ulong id => id,
        string text when ulong.TryParse(text, CultureInfo.InvariantCulture, out ulong id) => id,
        int number and >= 0 => (ulong)number,
        long number and >= 0 => (ulong)number,
        uint number => number,
        _ => null,
    };

    private void Show(ZabbixFrontend frontend, ZabbixPageTarget? given, ZabbixPageKind kind, ulong id)
    {
        ZabbixPageTarget target;
        if (given is not null && !NeedsRead(given))
        {
            target = given;
        }
        else
        {
            if (!TryRun(cancellationToken => ReadAsync(kind, id, cancellationToken), out ZabbixPageTarget? read, id)) return;
            if (read is null)
            {
                WriteError(new ErrorRecord(new ItemNotFoundException($"no {kind} has the id {id} on {Connected.Name}"), "ZabbixPageNotFound", ErrorCategory.ObjectNotFound, id));
                return;
            }
            target = read;
        }

        Uri page;
        try
        {
            page = frontend.UriFor(target, View);
        }
        catch (ArgumentException refused)
        {
            WriteError(new ErrorRecord(refused, "ZabbixPageRefused", ErrorCategory.InvalidArgument, target));
            return;
        }

        if (PassThru)
        {
            WriteObject(page);
            return;
        }
        WriteVerbose($"opening {page.AbsoluteUri}");
        if (ZabbixContext.OpenPage(page) is { } why)
        {
            WriteWarning($"the page was not opened: {why}; its address is written instead");
            WriteObject(page);
        }
    }

    // What the routes read besides the id, and a record read with the default output may lack: the name (7.0 filters
    // its lists by it), whether the object is on a host or a template and which, an action's event source, the trigger
    // of a trigger's event. One read by id fills them; a target with an unusable id is refused by UriFor instead.
    private static bool NeedsRead(ZabbixPageTarget target)
        => target.Id != 0
            && (string.IsNullOrEmpty(target.Name)
                || (target.Kind is ZabbixPageKind.Item or ZabbixPageKind.Trigger or ZabbixPageKind.Graph or ZabbixPageKind.DiscoveryRule or ZabbixPageKind.WebScenario
                    && (target.Context is null || target.HostId is null))
                || (target.Kind is ZabbixPageKind.Action && target.EventSource is null)
                || (target.Kind is ZabbixPageKind.Problem or ZabbixPageKind.Event && target.TriggerId is null && target.EventSource is null));

    private async Task<ZabbixPageTarget?> ReadAsync(ZabbixPageKind kind, ulong id, CancellationToken cancellationToken)
    {
        ZabbixClient api = ZabbixClient;
        ulong[] ids = [id];
        ZabbixOutput<ZabbixHost> hosts = s_hostsAndStatus;
        object? record = kind switch
        {
            ZabbixPageKind.Item => FirstOf(await ListAsync(api.Items.GetAsync(
                new ZabbixItemGetParameters { ItemIds = ids, Output = s_itemFields, SelectHosts = hosts }, cancellationToken), cancellationToken).ConfigureAwait(false)),
            ZabbixPageKind.DiscoveryRule => FirstOf(await ListAsync(api.DiscoveryRules.GetAsync(
                new ZabbixDiscoveryRuleGetParameters { ItemIds = ids, Output = s_discoveryRuleFields, SelectHosts = hosts }, cancellationToken), cancellationToken).ConfigureAwait(false)),
            ZabbixPageKind.Trigger => FirstOf(await ListAsync(api.Triggers.GetAsync(
                new ZabbixTriggerGetParameters { TriggerIds = ids, Output = s_triggerFields, SelectHosts = hosts }, cancellationToken), cancellationToken).ConfigureAwait(false)),
            ZabbixPageKind.Graph => FirstOf(await ListAsync(api.Graphs.GetAsync(
                new ZabbixGraphGetParameters { GraphIds = ids, Output = s_graphFields, SelectHosts = hosts }, cancellationToken), cancellationToken).ConfigureAwait(false)),
            ZabbixPageKind.WebScenario => FirstOf(await ListAsync(api.HttpTests.GetAsync(
                new ZabbixHttpTestGetParameters { HttpTestIds = ids, Output = s_webScenarioFields, SelectHosts = hosts }, cancellationToken), cancellationToken).ConfigureAwait(false)),
            ZabbixPageKind.Host => await ByIdAsync<ZabbixHost>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.HostGroup => await ByIdAsync<ZabbixHostGroup>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.Template => await ByIdAsync<ZabbixTemplate>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.TemplateGroup => await ByIdAsync<ZabbixTemplateGroup>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.Dashboard => await ByIdAsync<ZabbixDashboard>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.Map => await ByIdAsync<ZabbixMap>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.Problem => await ByIdAsync<ZabbixProblem>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.Event => await ByIdAsync<ZabbixEvent>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.Maintenance => await ByIdAsync<ZabbixMaintenanceRecord>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.User => await ByIdAsync<ZabbixUser>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.UserGroup => await ByIdAsync<ZabbixUserGroup>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.Proxy => await ByIdAsync<ZabbixProxy>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.NetworkDiscoveryRule => await ByIdAsync<ZabbixNetworkDiscoveryRule>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.Action => await ByIdAsync<ZabbixAction>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.MediaType => await ByIdAsync<ZabbixMediaType>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.Script => await ByIdAsync<ZabbixScript>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.Service => await ByIdAsync<ZabbixServiceRecord>(id, cancellationToken).ConfigureAwait(false),
            ZabbixPageKind.Sla => await ByIdAsync<ZabbixSlaRecord>(id, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a kind of page"),
        };
        return ZabbixPageTarget.TryFrom(record, out ZabbixPageTarget? target) ? target : null;
    }

    private async Task<object?> ByIdAsync<TEntity>(ulong id, CancellationToken cancellationToken)
        => FirstOf(await ZabbixEntities.Of<TEntity>().SelectByIdAsync(ZabbixClient, [id], cancellationToken).ConfigureAwait(false));

    private static object? FirstOf<T>(IReadOnlyList<T> found) => found.Count > 0 ? found[0] : null;
}
