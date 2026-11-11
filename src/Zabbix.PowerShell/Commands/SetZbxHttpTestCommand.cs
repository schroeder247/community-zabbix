// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;
using System.Security;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Changes a web scenario (<c>httptest.update</c>).</para>
/// <para type="description">
/// <c>-Id</c> takes the scenario's id, not its name; a piped object with an <c>HttpTestId</c> (e.g. from
/// <c>Get-ZbxHttpTest</c>) binds to it. Only the parameters you give are changed; <c>-Step</c> replaces all of the
/// scenario's steps. Reads the scenario back after the update and writes it. Supports <c>-WhatIf</c>/<c>-Confirm</c>
/// (ConfirmImpact Medium).
/// </para>
/// </summary>
/// <remarks>
/// Hand-written, not generated (see <see cref="NewZbxHttpTestCommand"/>). <c>-Id</c> takes the scenario's id
/// (<c>ZabbixHttpTest</c> carries no entity shape
/// to resolve a name or a piped object by, since <c>httptest.get</c>'s parameters cannot implement
/// <see cref="IZabbixGetParameters"/>).
/// </remarks>
[Cmdlet(VerbsCommon.Set, "ZbxHttpTest", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium)]
[OutputType(typeof(ZabbixHttpTest))]
public sealed class SetZbxHttpTestCommand : ZabbixServerCmdlet
{
    /// <summary>The scenario's id.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [Alias("HttpTestId")]
    public ulong Id { get; set; }

    /// <summary>The scenario's new name.</summary>
    [Parameter]
    public string? Name { get; set; }

    /// <summary>Seconds between executions.</summary>
    [Parameter]
    public string? Delay { get; set; }

    /// <summary>Retries on a failed step.</summary>
    [Parameter]
    public int? Retries { get; set; }

    /// <summary>The User-Agent string sent.</summary>
    [Parameter]
    public string? Agent { get; set; }

    /// <summary>Enabled or disabled.</summary>
    [Parameter]
    public ZabbixHttpTestStatus? Status { get; set; }

    /// <summary>The HTTP authentication method.</summary>
    [Parameter]
    public ZabbixHttpAuthentication? Authentication { get; set; }

    /// <summary>The HTTP authentication user.</summary>
    [Parameter]
    public string? HttpUser { get; set; }

    /// <summary>The HTTP authentication password.</summary>
    [Parameter]
    public SecureString? HttpPassword { get; set; }

    /// <summary>
    /// The steps, replacing the scenario's existing steps: what <c>New-ZbxHttpTestStep</c> writes,
    /// <c>ZabbixHttpTestUpdateStepRequest</c> objects, or hashtables keyed by property or API field name.
    /// </summary>
    [Parameter]
    [ZabbixHttpTestSteps<ZabbixHttpTestUpdateStepRequest>]
    public ZabbixHttpTestUpdateStepRequest[]? Step { get; set; }

    /// <summary>An HTTP proxy (<c>[protocol://][user[:password]@]proxy.example.com:1080</c>).</summary>
    [Parameter]
    public string? HttpProxy { get; set; }

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

    /// <summary>Changes without a confirmation prompt; <c>-WhatIf</c> and an explicit <c>-Confirm</c> still apply.</summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    /// <summary>Writes the changed scenario, read back after the update; without it the command writes nothing, like every generated <c>Set-</c>.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        string target = $"httptest {Id}";
        if (!ShouldProcessUnlessForced(target, "Set httptest", Force)) return;
        if (!TryRun(UpdateAndReadBackAsync, out IReadOnlyList<ZabbixHttpTest>? updated, target)) return;
        foreach (ZabbixHttpTest test in updated) WriteObject(test);
    }

    private async Task<IReadOnlyList<ZabbixHttpTest>> UpdateAndReadBackAsync(CancellationToken cancellationToken)
    {
        var request = ZabbixPropertyOverrides.Apply(new ZabbixHttpTestUpdateRequest
        {
            HttpTestId = Id,
            Name = Name,
            Delay = Delay,
            Retries = Retries,
            Agent = Agent,
            Status = Status,
            Authentication = Authentication,
            HttpUser = HttpUser,
            HttpPassword = HttpPassword is null ? null : new System.Net.NetworkCredential(string.Empty, HttpPassword).Password,
            HttpProxy = HttpProxy,
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
        ulong id = await ZabbixClient.HttpTests.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!PassThru) return [];
        var parameters = new ZabbixHttpTestGetParameters { HttpTestIds = [id] };
        return await ZabbixClient.HttpTests.GetAsync(parameters, cancellationToken).ConfigureAwait(false);
    }
}
