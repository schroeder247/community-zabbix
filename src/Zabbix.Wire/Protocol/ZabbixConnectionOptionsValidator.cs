// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Protocol;

/// <summary>
/// The generated validator for <see cref="ZabbixConnectionOptions"/>, named by the agents' and the proxy's
/// <c>[ValidateObjectMembers]</c> so the options generator does not synthesize a copy in each of their assemblies (the
/// copies clash, CS0436, through the family's InternalsVisibleTo).
/// </summary>
[OptionsValidator]
internal sealed partial class ZabbixConnectionOptionsValidator : IValidateOptions<ZabbixConnectionOptions>
{
}
