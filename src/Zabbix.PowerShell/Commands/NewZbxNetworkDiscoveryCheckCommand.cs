// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Net;
using System.Security;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Builds one network-discovery check (<c>dcheck</c>) for a discovery rule, typed rather than a hand-built hashtable of magic wire fields.</para>
/// <para type="description">
/// Writes the typed check (<c>ZabbixNetworkDiscoveryCheckRequest</c>), which <c>New-ZbxNetworkDiscoveryRule</c>/
/// <c>Set-ZbxNetworkDiscoveryRule -Check</c> take directly, alone or in an array; a script changes a field with
/// <c>$check with</c>-style copies or <c>-AdditionalProperty</c> (keyed by wire name, e.g. <c>@{ ports = '10050-10051' }</c>).
/// The SNMPv3 passphrases are carried for the wire but not shown by the default view. Nothing is sent to the server.
/// </para>
/// </summary>
/// <remarks>
/// <c>-Check</c> is a <c>ZabbixNetworkDiscoveryCheckRequest[]</c> (the <c>dchecks</c> table's columns), so the
/// check binds as it is. <c>-Type</c> is the SDK's own
/// <see cref="ZabbixNetworkDiscoveryCheckType"/>, never a restated enum, matching <c>ZabbixNetworkDiscoveryCheck.Type</c>.
/// </remarks>
[Cmdlet(VerbsCommon.New, "ZbxNetworkDiscoveryCheck")]
[OutputType(typeof(ZabbixNetworkDiscoveryCheckRequest))]
public sealed class NewZbxNetworkDiscoveryCheckCommand : ZabbixCmdlet
{
    /// <summary>
    /// The check kind (<c>type</c>): e.g. <c>Icmp</c>, <c>ZabbixAgent</c>, <c>SnmpV2c</c>, <c>SnmpV3</c>. <c>Tab</c>
    /// after <c>-Type</c> cycles through every kind.
    /// </summary>
    /// <remarks>
    /// Readable names (<c>schema/overlay.json</c>'s <c>"SVC"</c> entry overrides the terse <c>SVC_*</c>-derived
    /// defaults). PowerShell completes an enum-typed parameter's
    /// legal values natively (no <c>[ArgumentCompleter]</c> needed): <c>Tab</c> after <c>-Type</c> cycles the names
    /// above.
    /// </remarks>
    [Parameter(Mandatory = true, Position = 0)]
    public ZabbixNetworkDiscoveryCheckType Type { get; set; }

    /// <summary>The item key an agent/simple check runs (<c>key_</c>); ignored for an SNMP check, which takes <c>-SnmpOid</c> instead.</summary>
    [Parameter]
    public string? Key { get; set; }

    /// <summary>The SNMP OID to walk (<c>key_</c> on an SNMP check).</summary>
    [Parameter]
    public string? SnmpOid { get; set; }

    /// <summary>The SNMPv1/v2c community (<c>snmp_community</c>).</summary>
    [Parameter]
    public string? SnmpCommunity { get; set; }

    /// <summary>The SNMPv3 security name (<c>snmpv3_securityname</c>).</summary>
    [Parameter]
    public string? SnmpSecurityName { get; set; }

    /// <summary>The SNMPv3 security level (<c>snmpv3_securitylevel</c>).</summary>
    [Parameter]
    public ZabbixSnmpSecurityLevel? SnmpSecurityLevel { get; set; }

    /// <summary>The SNMPv3 authentication passphrase (<c>snmpv3_authpassphrase</c>).</summary>
    [Parameter]
    public SecureString? SnmpAuthenticationPassphrase { get; set; }

    /// <summary>The SNMPv3 authentication protocol (<c>snmpv3_authprotocol</c>).</summary>
    [Parameter]
    public ZabbixSnmpAuthenticationProtocol? SnmpAuthenticationProtocol { get; set; }

    /// <summary>The SNMPv3 privacy passphrase (<c>snmpv3_privpassphrase</c>).</summary>
    [Parameter]
    public SecureString? SnmpPrivacyPassphrase { get; set; }

    /// <summary>The SNMPv3 privacy protocol (<c>snmpv3_privprotocol</c>).</summary>
    [Parameter]
    public ZabbixSnmpPrivacyProtocol? SnmpPrivacyProtocol { get; set; }

    /// <summary>The SNMPv3 context name (<c>snmpv3_contextname</c>).</summary>
    [Parameter]
    public string? SnmpContextName { get; set; }

    /// <summary>A single port to check (<c>ports</c>); overridden by <c>-Ports</c> when both are given.</summary>
    [Parameter]
    [ValidateRange(1, 65535)]
    public int? Port { get; set; }

    /// <summary>A port range/list to check (<c>ports</c>), e.g. <c>"22-23"</c>; takes precedence over <c>-Port</c>.</summary>
    [Parameter]
    public string? Ports { get; set; }

    /// <summary>Marks this check as the rule's uniqueness criterion (<c>uniq</c>): the field that decides whether two discovered devices are the same host.</summary>
    [Parameter]
    public SwitchParameter Unique { get; set; }

    /// <summary>
    /// Extra fields set on the built check last, keyed by their exact API field names; they win over every parameter
    /// above.
    /// </summary>
    [Parameter]
    public Hashtable? AdditionalProperty { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        bool isSnmp = Type is ZabbixNetworkDiscoveryCheckType.SnmpV1 or ZabbixNetworkDiscoveryCheckType.SnmpV2c or ZabbixNetworkDiscoveryCheckType.SnmpV3;
        var check = new ZabbixNetworkDiscoveryCheckRequest
        {
            Type = Type,
            Unique = Unique.IsPresent ? 1 : 0,
            Key = isSnmp ? SnmpOid : Key,
            SnmpCommunity = SnmpCommunity,
            Ports = Ports ?? Port?.ToString(CultureInfo.InvariantCulture),
            Snmpv3SecurityName = SnmpSecurityName,
            Snmpv3SecurityLevel = SnmpSecurityLevel,
            Snmpv3AuthenticationPassphrase = SnmpAuthenticationPassphrase is null ? null : new NetworkCredential(string.Empty, SnmpAuthenticationPassphrase).Password,
            Snmpv3AuthenticationProtocol = SnmpAuthenticationProtocol,
            Snmpv3PrivacyPassphrase = SnmpPrivacyPassphrase is null ? null : new NetworkCredential(string.Empty, SnmpPrivacyPassphrase).Password,
            Snmpv3PrivacyProtocol = SnmpPrivacyProtocol,
            Snmpv3ContextName = SnmpContextName,
        };
        try
        {
            check = ZabbixPropertyOverrides.Apply(check, AdditionalProperty);
        }
        catch (ArgumentException refused)
        {
            // A -AdditionalProperty key or value that does not fit the check is a usage error, terminating like a binding error.
            ThrowTerminatingError(ZabbixErrors.From(refused, AdditionalProperty));
        }
        WriteObject(check);
    }
}
