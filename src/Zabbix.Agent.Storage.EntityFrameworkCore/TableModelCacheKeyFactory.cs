// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Community.Zabbix.Storage.EntityFrameworkCore;

/// <summary>
/// EF caches one model per context type; a context whose table varies per instance keys the cache on the table too. This is
/// EF's designated seam for exactly that (<see cref="IModelCacheKeyFactory"/>).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "EF Core's service provider instantiates it from ReplaceService<IModelCacheKeyFactory, TableModelCacheKeyFactory>().")]
internal sealed class TableModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => context is IRecordTable table ? (context.GetType(), table.Table, designTime) : (context.GetType(), designTime);
}
