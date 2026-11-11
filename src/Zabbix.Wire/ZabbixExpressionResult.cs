// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>One evaluated trigger expression.</summary>
/// <param name="Expression">The expression as sent.</param>
/// <param name="Value">1 true, 0 false; absent when it did not evaluate.</param>
/// <param name="Error">Why it did not evaluate.</param>
public sealed record ZabbixExpressionResult(
    [property: JsonPropertyName("expression")] string? Expression,
    [property: JsonPropertyName("value")] int? Value,
    [property: JsonPropertyName("error")] string? Error)
{
    /// <summary>Whether the expression is true.</summary>
    public bool IsTrue => Value == 1;
}
