// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Updates the server's authentication settings (<c>authentication.update</c>).</para>
/// </summary>
[Cmdlet(VerbsCommon.Set, "ZbxAuthentication", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium)]
[OutputType(typeof(ZabbixAuthenticationSettings))]
public sealed class SetZbxAuthenticationCommand : ZabbixSingletonSetCmdlet<ZabbixAuthenticationUpdateRequest>
{
    /// <summary>Which authentication method is default (<c>authentication_type</c>).</summary>
    [Parameter] public ZabbixAuthenticationType? AuthenticationType { get; set; }

    /// <summary>Whether LDAP authentication is enabled (<c>ldap_auth_enabled</c>).</summary>
    [Parameter] public bool? LdapAuthenticationEnabled { get; set; }

    /// <summary>Whether LDAP logins are case-sensitive (<c>ldap_case_sensitive</c>).</summary>
    [Parameter] public ZabbixAuthenticationCase? LdapCaseSensitive { get; set; }

    /// <summary>The default LDAP user directory (<c>ldap_userdirectoryid</c>).</summary>
    [Parameter] public ulong? LdapUserDirectoryId { get; set; }

    /// <summary>Whether SAML authentication is enabled (<c>saml_auth_enabled</c>).</summary>
    [Parameter] public bool? SamlAuthenticationEnabled { get; set; }

    /// <summary>Whether SAML logins are case-sensitive (<c>saml_case_sensitive</c>).</summary>
    [Parameter] public ZabbixAuthenticationCase? SamlCaseSensitive { get; set; }

    /// <summary>The minimum password length (<c>passwd_min_length</c>).</summary>
    [Parameter] public int? PasswordMinLength { get; set; }

    /// <summary>Which password strength rules apply (<c>passwd_check_rules</c>).</summary>
    [Parameter] public ZabbixPasswordCheckRules? PasswordCheckRules { get; set; }

    /// <summary>The user group a JIT-provisioned but unmapped user lands in (<c>disabled_usrgrpid</c>).</summary>
    [Parameter] public ulong? DisabledUserGroupId { get; set; }

    /// <summary>How often JIT-provisioned users are refreshed (<c>jit_provision_interval</c>).</summary>
    [Parameter] public string? JitProvisionInterval { get; set; }

    /// <summary>Whether SAML just-in-time provisioning is enabled (<c>saml_jit_status</c>).</summary>
    [Parameter] public bool? SamlJitStatus { get; set; }

    /// <summary>Whether LDAP just-in-time provisioning is enabled (<c>ldap_jit_status</c>).</summary>
    [Parameter] public bool? LdapJitStatus { get; set; }

    /// <summary>Whether multi-factor authentication is enabled (<c>mfa_status</c>).</summary>
    [Parameter] public bool? MfaStatus { get; set; }

    /// <summary>The default MFA method (<c>mfaid</c>).</summary>
    [Parameter] public ulong? MfaId { get; set; }

    /// <summary>Whether HTTP authentication is enabled (<c>http_auth_enabled</c>).</summary>
    [Parameter] public bool? HttpAuthenticationEnabled { get; set; }

    /// <summary>Where the HTTP login form appears (<c>http_login_form</c>).</summary>
    [Parameter] public ZabbixHttpLoginForm? HttpLoginForm { get; set; }

    /// <summary>Domains stripped from an HTTP-authenticated username (<c>http_strip_domains</c>).</summary>
    [Parameter] public string? HttpStripDomains { get; set; }

    /// <summary>Whether HTTP logins are case-sensitive (<c>http_case_sensitive</c>).</summary>
    [Parameter] public ZabbixAuthenticationCase? HttpCaseSensitive { get; set; }

    /// <inheritdoc/>
    protected override string Subject => "the authentication settings";

    /// <inheritdoc/>
    protected override ZabbixAuthenticationUpdateRequest BuildRequest() => new()
    {
        AuthenticationType = AuthenticationType,
        LdapAuthenticationEnabled = LdapAuthenticationEnabled,
        LdapCaseSensitive = LdapCaseSensitive,
        LdapUserDirectoryId = LdapUserDirectoryId,
        SamlAuthenticationEnabled = SamlAuthenticationEnabled,
        SamlCaseSensitive = SamlCaseSensitive,
        PasswordMinLength = PasswordMinLength,
        PasswordCheckRules = PasswordCheckRules,
        DisabledUserGroupId = DisabledUserGroupId,
        JitProvisionInterval = JitProvisionInterval is null ? null : ZabbixDuration.Parse(JitProvisionInterval),
        SamlJitStatus = SamlJitStatus,
        LdapJitStatus = LdapJitStatus,
        MfaStatus = MfaStatus,
        MfaId = MfaId,
        HttpAuthenticationEnabled = HttpAuthenticationEnabled,
        HttpLoginForm = HttpLoginForm,
        HttpStripDomains = HttpStripDomains,
        HttpCaseSensitive = HttpCaseSensitive,
    };

    /// <inheritdoc/>
    protected override async Task<object?> UpdateAsync(ZabbixClient client, ZabbixAuthenticationUpdateRequest request, CancellationToken cancellationToken)
        => await client.Authentication.UpdateAsync(request, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    protected override async Task<object?> GetAsync(CancellationToken cancellationToken)
        => await ZabbixClient.Authentication.GetAsync(cancellationToken).ConfigureAwait(false);
}
