// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Storage.EntityFrameworkCore;

/// <summary>A context that names its table.</summary>
internal interface IRecordTable
{
    string Table { get; }
}
