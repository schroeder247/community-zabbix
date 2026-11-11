// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Analyzers;

/// <summary>
/// Blocking on a task in a SYNCHRONOUS method. CA1849 reports only inside async methods (measured), so
/// the banned-symbol file is what makes each of these an error.
/// </summary>
public static class SyncOverAsyncProbe
{
    /// <summary>Blocks three ways.</summary>
    /// <param name="work">A task.</param>
    /// <param name="value">A task with a result.</param>
    /// <returns>Its result.</returns>
    public static int Block(Task work, Task<int> value)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(value);
        work.Wait();                                                                        // expect: RS0030
        int first = value.Result;                                                           // expect: RS0030
        return first + value.GetAwaiter().GetResult();                                      // expect: RS0030
    }
}
