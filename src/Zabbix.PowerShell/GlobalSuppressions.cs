// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;

// A cmdlet parameter that takes several values is an array: that is the shape PowerShell binds `-Name a, b` and a
// pipeline of values to (Learn, "Cmdlet parameters": a parameter that accepts more than one value is declared as an
// array). The rule's concern — a property handing out a mutable internal array — does not apply to a bound parameter.
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
    Scope = "namespaceanddescendants", Target = "~N:Community.Zabbix.PowerShell",
    Justification = "PowerShell binds multi-valued parameters to arrays; ModuleShapeTests.Every_array_or_settable_collection_property_is_a_parameter keeps every such property a parameter.")]

// A cmdlet parameter is set by PowerShell's binder: a hashtable parameter (-AdditionalProperty, -Search, -ArgumentList) has an
// ordinary setter like every other parameter. The rule's concern — a caller replacing
// a collection an object owns — does not apply to a bound parameter.
[assembly: SuppressMessage("Usage", "CA2227:Collection properties should be read only",
    Scope = "namespaceanddescendants", Target = "~N:Community.Zabbix.PowerShell",
    Justification = "PowerShell's parameter binder sets cmdlet parameters, hashtables included; ModuleShapeTests.Every_array_or_settable_collection_property_is_a_parameter keeps every such property a parameter.")]
