// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>What an item key parameter was written as, in upstream's words (<c>zbx_request_parameter_type_t</c>).</summary>
public enum ZabbixItemKeyParameterType
{
    /// <summary>Nothing was written: <c>k[,x]</c>'s first parameter (<c>REQUEST_PARAMETER_TYPE_UNDEFINED</c>).</summary>
    Undefined = 0,

    /// <summary>A quoted or unquoted string (<c>REQUEST_PARAMETER_TYPE_STRING</c>).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "Upstream's own name for the kind (REQUEST_PARAMETER_TYPE_STRING); the member names what was written, not a .NET type.")]
    String = 1,

    /// <summary>An array, <c>[80,443]</c>; its value is the text between the brackets (<c>REQUEST_PARAMETER_TYPE_ARRAY</c>).</summary>
    Array = 2,
}
