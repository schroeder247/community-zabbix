// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Items;

/// <summary>What the <see cref="ZabbixItemCollector"/> asks of a family of sampled series.</summary>
internal interface IZabbixSamples
{
    /// <summary>Whether no key is asking for any of the family's series.</summary>
    bool IsEmpty { get; }

    /// <summary>Drops the series no key has asked for lately, and takes one sample of each of the others.</summary>
    /// <param name="now">The sample's stamp, on the collector's clock.</param>
    void Sample(long now);
}
