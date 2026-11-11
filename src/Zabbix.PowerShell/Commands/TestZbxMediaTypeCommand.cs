// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Sends a test message through a media type (<c>alert.send</c>).</para>
/// </summary>
[Cmdlet(VerbsDiagnostic.Test, "ZbxMediaType", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium)]
[OutputType("Community.Zabbix.PowerShell.MediaTypeTestResult")]
public sealed class TestZbxMediaTypeCommand : ZabbixServerWireCmdlet
{
    /// <summary>The media type: an id, a name, or the object (<c>Get-ZbxMediaType | Test-ZbxMediaType</c> binds its <c>MediaTypeId</c>).</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [Alias("Id", "MediaTypeId")]
    [ZabbixReference<ZabbixMediaType>]
    [ZabbixCompleter<ZabbixMediaType>]
    public ZabbixReference<ZabbixMediaType>? MediaType { get; set; }

    /// <summary>The recipient.</summary>
    [Parameter(Mandatory = true)]
    [ValidateNotNullOrWhiteSpace]
    public string? SendTo { get; set; }

    /// <summary>The message subject.</summary>
    [Parameter]
    public string Subject { get; set; } = "Test";

    /// <summary>The message body.</summary>
    [Parameter]
    public string Message { get; set; } = "Test message";

    /// <summary>
    /// A webhook's parameters (a hashtable, name to value) or a script's (values in order). When omitted, the media type's
    /// own saved parameters are sent, as the frontend's Test form sends them.
    /// </summary>
    [Parameter]
    public object? Parameter { get; set; }

    /// <summary>
    /// The server's trapper: <c>host</c> or <c>host:port</c> (port 10051 when omitted; an IPv6 address with a port is
    /// bracketed, <c>[::1]:10051</c>). Several are the nodes of one HA cluster, tried in order. The connection's
    /// (<c>Connect-ZbxServer -Trapper</c>), else the active HA node's, when omitted.
    /// </summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    [ZabbixEndPoint]
    public DnsEndPoint[]? Server { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (!ShouldProcess($"{SendTo} via media type {MediaType}", "Send test alert")) return;
        if (TryRun(async cancellationToken =>
            {
                IReadOnlyList<ulong> ids = await ResolveAsync([MediaType!], cancellationToken).ConfigureAwait(false);
                if (ids.Count == 0) return null;
                ZabbixAlertParameters? parameters = Given(Parameter) ?? await SavedParametersAsync(ids[0], cancellationToken).ConfigureAwait(false);
                return await CallWireAsync(Server, (wire, token) => wire.SendAlertAsync(ids[0], SendTo!, Subject, Message, parameters, token), cancellationToken).ConfigureAwait(false);
            }, out ZabbixServerReply? reply, MediaType) && reply is not null)
        {
            WriteObject(ZabbixJsonShape.ToShell(reply.Data ?? ZabbixServerReplies.Envelope(reply), "Community.Zabbix.PowerShell.MediaTypeTestResult"));
        }
    }

    // A hashtable is a webhook's parameters by name; anything else enumerable is a script's in order.
    private static ZabbixAlertParameters? Given(object? parameters) => (parameters is PSObject wrapped ? wrapped.BaseObject : parameters) switch
    {
        null => null,
        System.Collections.IDictionary named => ZabbixAlertParameters.Webhook(named.Cast<System.Collections.DictionaryEntry>()
            .Select(entry => new KeyValuePair<string, string>(LanguagePrimitives.ConvertTo<string>(entry.Key), LanguagePrimitives.ConvertTo<string>(entry.Value)))),
        string one => ZabbixAlertParameters.Script([one]),
        System.Collections.IEnumerable values => ZabbixAlertParameters.Script(values.Cast<object?>().Select(value => LanguagePrimitives.ConvertTo<string>(value))),
        { } one => ZabbixAlertParameters.Script([LanguagePrimitives.ConvertTo<string>(one)]),
    };

    // The frontend's Test form starts from the media type's saved parameters (CControllerMediatypeTestSend): a script's
    // values in their sort order, a webhook's by name; the other types have none.
    private async Task<ZabbixAlertParameters?> SavedParametersAsync(ulong mediaTypeId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ZabbixMediaType> found = await ZabbixClient.MediaTypes.GetAsync(new ZabbixMediaTypeGetParameters { MediaTypeIds = [mediaTypeId] }, cancellationToken).ConfigureAwait(false);
        return found is [{ Parameters: { Count: > 0 } saved, Type: var kind }]
            ? kind switch
            {
                ZabbixMediaTypeKind.Webhook => ZabbixAlertParameters.Webhook(saved.Select(parameter => new KeyValuePair<string, string>(parameter.Name ?? string.Empty, parameter.Value ?? string.Empty))),
                ZabbixMediaTypeKind.Exec => ZabbixAlertParameters.Script(saved.OrderBy(parameter => parameter.Position ?? 0).Select(parameter => parameter.Value ?? string.Empty)),
                _ => null,
            }
            : null;
    }
}
