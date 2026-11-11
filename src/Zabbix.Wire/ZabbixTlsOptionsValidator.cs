// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// The generated validator for <see cref="ZabbixTlsOptions"/>, named by every <c>[ValidateObjectMembers]</c> that nests
/// the TLS words. Without it the options generator synthesizes a private <c>__ZabbixTlsOptionsValidator__</c> in each
/// assembly that nests them, and those internal copies clash (CS0436) wherever one family assembly sees another's
/// internals.
/// </summary>
[OptionsValidator]
internal sealed partial class ZabbixTlsOptionsValidator : IValidateOptions<ZabbixTlsOptions>
{
}
