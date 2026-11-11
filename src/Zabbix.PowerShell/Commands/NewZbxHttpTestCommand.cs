// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;
using System.Security;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Creates a web scenario (<c>httptest.create</c>).</para>
/// <para type="description">
/// Creates the scenario on <c>-HostReference</c>, then reads it back (<c>httptest.get</c>) and writes the created
/// <c>ZabbixHttpTest</c>; there is no <c>-PassThru</c>. <c>-Step</c> takes <c>ZabbixHttpTestStepRequest</c> objects
/// (e.g. from <c>New-ZbxHttpTestStep</c>) or plain hashtables whose keys are either the property names or the API's
/// field names, e.g. <c>@{name='Homepage'; url='http://localhost/'; status_codes='200'}</c>. Supports
/// <c>-WhatIf</c>/<c>-Confirm</c> (ConfirmImpact Medium).
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// Hand-written, not generated: <c>ZabbixHttpTestGetParameters</c> carries <c>filter</c>/<c>search</c> but no
/// <c>searchWildcardsEnabled</c>, so it cannot implement <see cref="IZabbixGetParameters"/>, the entity never enters
/// the generated table, and <c>ZabbixNewCmdlet&lt;TEntity&gt;</c> does not fit it. It follows the same contract by
/// hand: creates, then reads the created object(s) back in one <c>httptest.get</c> and writes the entity.
/// </para>
/// <para>
/// A <c>-Step</c> hashtable is built into a step at binding, through the step's own JSON contract
/// (<see cref="ZabbixHttpTestStepsAttribute{TStep}"/>).
/// </para>
/// </remarks>
[Cmdlet(VerbsCommon.New, "ZbxHttpTest", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium)]
[OutputType(typeof(ZabbixHttpTest))]
public sealed class NewZbxHttpTestCommand : ZabbixServerCmdlet
{
    /// <summary>The scenario's name.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The host the scenario belongs to: an id, a technical name, or a <c>ZabbixHost</c> object.</summary>
    [Parameter(Mandatory = true, Position = 1)]
    [Alias("Host", "HostId")]
    [ZabbixReference<ZabbixHost>]
    [ZabbixCompleter<ZabbixHost>]
    public ZabbixReference<ZabbixHost>? HostReference { get; set; }

    /// <summary>
    /// The steps, in order: what <c>New-ZbxHttpTestStep</c> writes, <c>ZabbixHttpTestStepRequest</c> objects, or
    /// hashtables keyed by property or API field name. A step that names no number (<c>no</c>) is numbered by its
    /// position, 1 for the first.
    /// </summary>
    [Parameter(Mandatory = true, Position = 2)]
    [ZabbixHttpTestSteps<ZabbixHttpTestStepRequest>(NumberByPosition = true)]
    public ZabbixHttpTestStepRequest[] Step { get; set; } = [];

    /// <summary>Seconds between executions (e.g. <c>60s</c>).</summary>
    [Parameter]
    public string? Delay { get; set; }

    /// <summary>Retries on a failed step.</summary>
    [Parameter]
    public int? Retries { get; set; }

    /// <summary>The User-Agent string sent.</summary>
    [Parameter]
    public string? Agent { get; set; }

    /// <summary>An HTTP proxy (<c>[protocol://][user[:password]@]proxy.example.com:1080</c>).</summary>
    [Parameter]
    public string? HttpProxy { get; set; }

    /// <summary>Enabled or disabled.</summary>
    [Parameter]
    public ZabbixHttpTestStatus? Status { get; set; }

    /// <summary>HTTP authentication method.</summary>
    [Parameter]
    public ZabbixHttpAuthentication? Authentication { get; set; }

    /// <summary>The HTTP authentication username.</summary>
    [Parameter]
    public string? HttpUser { get; set; }

    /// <summary>The HTTP authentication password.</summary>
    [Parameter]
    public SecureString? HttpPassword { get; set; }

    /// <summary>Verify the peer's SSL certificate.</summary>
    [Parameter]
    public bool? VerifyPeer { get; set; }

    /// <summary>Verify the host name in the peer's SSL certificate.</summary>
    [Parameter]
    public bool? VerifyHost { get; set; }

    /// <summary>The SSL client certificate file (<c>ssl_cert_file</c>), relative to the server's <c>SSLCertLocation</c>.</summary>
    [Parameter]
    public string? SslCertFile { get; set; }

    /// <summary>The SSL private key file (<c>ssl_key_file</c>), relative to the server's <c>SSLKeyLocation</c>.</summary>
    [Parameter]
    public string? SslKeyFile { get; set; }

    /// <summary>The SSL private key's password (<c>ssl_key_password</c>).</summary>
    [Parameter]
    public SecureString? SslKeyPassword { get; set; }

    /// <summary>Scenario variables (<c>variables</c>): a hashtable of <c>{macro}</c> name to value, or <c>{name, value}</c> records.</summary>
    [Parameter]
    [ZabbixNameValueList<ZabbixHttpTestVariableRequest>("name")]
    public ZabbixHttpTestVariableRequest[]? Variable { get; set; }

    /// <summary>Request headers sent by every step (<c>headers</c>): a hashtable of header name to value (<c>[ordered]</c> keeps the order), or <c>{name, value}</c> records.</summary>
    [Parameter]
    [ZabbixNameValueList<ZabbixHttpTestHeaderRequest>("name")]
    public ZabbixHttpTestHeaderRequest[]? Header { get; set; }

    /// <summary>The scenario's tags (<c>tags</c>): a hashtable of tag name to value, or <c>{tag, value}</c> records.</summary>
    [Parameter]
    [ZabbixNameValueList<ZabbixTagRequest>("tag")]
    public ZabbixTagRequest[]? Tag { get; set; }

    /// <summary>Extra fields merged onto the request last: keys match a request property by name, case-insensitively.</summary>
    [Parameter]
    public Hashtable? AdditionalProperty { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (!ShouldProcess("new httptest", "New httptest")) return;
        if (!TryRun(CreateAndReadBackAsync, out IReadOnlyList<ZabbixHttpTest>? created, Name)) return;
        foreach (ZabbixHttpTest test in created) WriteObject(test);
    }

    private async Task<IReadOnlyList<ZabbixHttpTest>> CreateAndReadBackAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ulong> hostIds = await ResolveAsync([HostReference!], cancellationToken).ConfigureAwait(false);
        var request = ZabbixPropertyOverrides.Apply(new ZabbixHttpTestCreateRequest
        {
            HostId = hostIds[0],
            Name = Name,
            Delay = Delay,
            Retries = Retries,
            Agent = Agent,
            HttpProxy = HttpProxy,
            Status = Status,
            Authentication = Authentication,
            HttpUser = HttpUser,
            HttpPassword = HttpPassword is null ? null : new System.Net.NetworkCredential(string.Empty, HttpPassword).Password,
            VerifyPeer = VerifyPeer,
            VerifyHost = VerifyHost,
            SslCertFile = SslCertFile,
            SslKeyFile = SslKeyFile,
            SslKeyPassword = SslKeyPassword is null ? null : new System.Net.NetworkCredential(string.Empty, SslKeyPassword).Password,
            Variables = Variable,
            Headers = Header,
            Tags = Tag,
            Steps = Step,
        }, AdditionalProperty);
        ulong id = await ZabbixClient.HttpTests.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        var parameters = new ZabbixHttpTestGetParameters { HttpTestIds = [id] };
        return await ZabbixClient.HttpTests.GetAsync(parameters, cancellationToken).ConfigureAwait(false);
    }
}
