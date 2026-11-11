// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix.Tests;

/// <summary>
/// The one way a test builds a container: with the container's own validation switched on, so a captive
/// dependency or an unconstructible registration fails the test that composed it rather than the first
/// request that resolves it (the host enables these only in Development since .NET 9).
/// </summary>
internal static class ServiceCollectionTesting
{
    public static ServiceProvider BuildValidated(this IServiceCollection services)
        => services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
}
