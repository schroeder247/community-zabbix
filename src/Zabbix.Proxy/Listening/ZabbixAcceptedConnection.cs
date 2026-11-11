// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.IO.Pipelines;

namespace Community.Zabbix.Listening;

/// <summary>An admitted connection: where the frame is read and the reply written, how it is protected, what to dispose.</summary>
internal readonly record struct ZabbixAcceptedConnection(IDuplexPipe Transport, ZabbixTlsAcceptModes Security, Stream? Owned);
