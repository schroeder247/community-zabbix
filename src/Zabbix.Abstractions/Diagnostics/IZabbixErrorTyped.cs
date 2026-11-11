// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;

namespace Community.Zabbix.Diagnostics;

/// <summary>A failure that knows its own low-cardinality <c>error.type</c> (a Zabbix API error kind, a refused reply).</summary>
internal interface IZabbixErrorTyped
{
    /// <summary>The <c>error.type</c> value.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    string ErrorType { get; }
}
