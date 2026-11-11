// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
// What each callback is, by a name that reads better than its Func in this project's own code. The public surface is the Func.
global using ZabbixExchangeCallback = System.Func<System.ReadOnlyMemory<byte>, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<System.ReadOnlyMemory<byte>>>;
global using ZabbixNotificationCallback = System.Func<System.ReadOnlyMemory<byte>, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask>;
