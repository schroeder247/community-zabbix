// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>Reads a listener's refusal (event 6003) by its rejection code, never by its prose.</summary>
internal static class ListenerRefusals
{
    /// <summary>Whether <paramref name="record"/> is event 6003 with the rejection code <paramref name="rejection"/>.</summary>
    public static bool Is(FakeLogRecord record, string rejection)
        => record.Id.Id == 6003 && record.StructuredState?.SingleOrDefault(state => state.Key == "Rejection").Value == rejection;
}
