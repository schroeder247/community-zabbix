// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>One typed value of an item, and when it was collected.</summary>
/// <typeparam name="T">The value's type.</typeparam>
/// <param name="Time">When the value was collected.</param>
/// <param name="Value">The value it held.</param>
public readonly record struct ZabbixSample<T>(DateTimeOffset Time, T Value);
