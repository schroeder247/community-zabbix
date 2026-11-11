// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Connector;

/// <summary>A streamed value's type (upstream's <c>ITEM_VALUE_TYPE_*</c>), which decides the JSON form of its value.</summary>
public enum ZabbixConnectorValueType
{
    /// <summary><c>ITEM_VALUE_TYPE_FLOAT</c>: a number.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "Zabbix's own word for the value type (\"Numeric (float)\"); the member names what upstream streams, not a .NET type.")]
    Float = 0,

    /// <summary><c>ITEM_VALUE_TYPE_STR</c>: a character string.</summary>
    Character = 1,

    /// <summary><c>ITEM_VALUE_TYPE_LOG</c>: a log line, with its own timestamp.</summary>
    Log = 2,

    /// <summary><c>ITEM_VALUE_TYPE_UINT64</c>: an unsigned integer.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "Zabbix's own word for the value type (\"Numeric (unsigned)\"); the member names what upstream streams, not a .NET type.")]
    Unsigned = 3,

    /// <summary><c>ITEM_VALUE_TYPE_TEXT</c>.</summary>
    Text = 4,

    /// <summary><c>ITEM_VALUE_TYPE_BIN</c>: binary, base64.</summary>
    Binary = 5,
}
