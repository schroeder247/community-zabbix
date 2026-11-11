// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Hosting;

/// <summary>One report from a send loop: what became of a batch, and when.</summary>
/// <param name="Outcome">What became of it.</param>
/// <param name="Count">How many values it held.</param>
/// <param name="At">When, on the loop's clock.</param>
public readonly record struct ZabbixSendProgress(ZabbixSendOutcome Outcome, int Count, DateTimeOffset At);
