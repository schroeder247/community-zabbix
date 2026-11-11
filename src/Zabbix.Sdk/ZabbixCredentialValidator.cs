// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Says a client's credential was given in code, by its builder's last credential verb: the credential itself when it's an
/// instance, or <see langword="null"/> when the container builds it.
/// </summary>
/// <param name="Credential">The credential given, when it's known before the container builds it.</param>
internal sealed record ZabbixCredentialInCode(ZabbixCredential? Credential);

/// <summary>
/// Checks a client's options against the credential its builder gave in code: never beside the options' own
/// <see cref="ZabbixOptions.ApiToken"/>, and never over plain <c>http</c> to another host unless the options allow it. Only the
/// last credential verb counts, so a later one replaces what an earlier one gave.
/// </summary>
/// <param name="services">The container, which holds the client's <see cref="ZabbixCredentialInCode"/> under its name.</param>
internal sealed class ZabbixCredentialValidator(IServiceProvider services) : IValidateOptions<ZabbixOptions>
{
    public ValidateOptionsResult Validate(string? name, ZabbixOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (services.GetKeyedService<ZabbixCredentialInCode>(name ?? Options.DefaultName) is not { } given) return ValidateOptionsResult.Skip;

        List<string> failures = [];
        if (!string.IsNullOrEmpty(options.ApiToken))
            failures.Add("The options carry an ApiToken beside the credential given in code; give one of them.");
        if (given.Credential is { } credential && !ReferenceEquals(credential, ZabbixCredential.Anonymous)
            && !ZabbixOptions.AllowsCredentialsTo(options.Endpoint, options.AllowUnencryptedCredentials))
        {
            failures.Add(ZabbixOptions.UnencryptedCredential);
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
