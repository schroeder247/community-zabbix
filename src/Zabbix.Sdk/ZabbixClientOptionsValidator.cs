// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>The generated validator for <see cref="ZabbixClientOptions"/>: a bad document fails at start, not at the first request.</summary>
[OptionsValidator]
internal sealed partial class ZabbixClientOptionsValidator : IValidateOptions<ZabbixClientOptions>
{
}
