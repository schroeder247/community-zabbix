// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Diagnostics;

/// <summary>
/// When a running count is logged: a store, a watcher or a poll that drops records counts every drop on its meter and logs
/// only at the 1st, 10th, 100th, … one, so a sustained overflow is said without one line per record.
/// </summary>
internal static class ZabbixLogSampling
{
    /// <summary>Whether <paramref name="count"/> is 1, 10, 100, ….</summary>
    public static bool IsPowerOfTen(long count)
    {
        while (count >= 10 && count % 10 == 0) count /= 10;
        return count == 1;
    }
}
