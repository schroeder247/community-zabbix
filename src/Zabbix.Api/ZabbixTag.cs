// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>A tag as a read returns it, and the request that sets it again.</summary>
public sealed partial record ZabbixTag
{
    /// <summary>Creates the request that sets this tag, for a read-modify-write of an object's tags.</summary>
    /// <returns>A <see cref="ZabbixTagRequest"/> with this tag's name and value; an absent value is empty.</returns>
    /// <exception cref="InvalidOperationException">The tag has no name; a read that did not select the <c>tag</c> field returns none.</exception>
    public ZabbixTagRequest ToRequest()
        => Tag is null
            ? throw new InvalidOperationException("The tag has no name, so it cannot be set again; select the tag field when reading it.")
            : new() { Tag = Tag, Value = Value ?? string.Empty };
}
