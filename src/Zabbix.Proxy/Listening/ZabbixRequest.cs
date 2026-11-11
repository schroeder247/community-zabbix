// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;

namespace Community.Zabbix.Listening;

/// <summary>One framed request as the listener hands it to a handler.</summary>
/// <param name="Payload">The frame's payload.</param>
/// <param name="Peer">Who sent it.</param>
public readonly record struct ZabbixRequest(ReadOnlyMemory<byte> Payload, EndPoint? Peer);
