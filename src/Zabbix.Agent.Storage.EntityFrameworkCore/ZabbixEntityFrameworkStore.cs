// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Community.Zabbix.Storage.EntityFrameworkCore;

/// <summary>
/// Represents a durable store on EF Core. The columns are generated from the record; the provider is EF's own options
/// builder, so any database works. The table is named after the store (<see cref="ZabbixStoreServices.Name"/>) and
/// created at first use, not in the constructor. A push agent's builder uses one for each destination with
/// <c>UseEntityFrameworkStore(db =&gt; db.UseNpgsql(cs))</c>.
/// </summary>
/// <remarks>
/// <para>The sequence is the table's identity key (<c>__seq</c>, a PostgreSQL identity column, SQLite
/// <c>AUTOINCREMENT</c>), so it is written with the record and never reused, even after the table empties: a restarted
/// process continues above every id it ever handed out. The stored time is <c>__stored</c> (UTC ticks).</para>
/// <para>Every database call is EF's asynchronous form, made by the store's own loop (<see cref="ZabbixPersistentStore{T}"/>),
/// never on a writer's or a reader's thread: what was written while a commit was under way goes in the next one, one
/// <c>SaveChangesAsync</c>; an acknowledgement is one <c>DELETE … WHERE __seq &lt;= n</c>; records are loaded
/// <see cref="ZabbixPersistentStore{T}.ReadAhead"/> at a time. A leased record stays in the table until it is acknowledged, so a
/// batch in flight when the process stops is leased again, with the same sequences, after the restart.</para>
/// <para>The table's name is <c>zbx_store_</c>, the store's name folded to lower-case letters, digits and <c>_</c>, and a
/// short hash of the name as given, so two names that fold alike (<c>a-b</c>, <c>a_b</c>) never share a table and the name
/// fits PostgreSQL's 63 bytes. A table that already exists must carry the record's columns: one that does not (a table
/// left by another record type, or by an earlier shape of this one) fails the first use, rather than every insert after
/// it. One store owns its table: two processes on one table lease the same rows.</para>
/// </remarks>
/// <typeparam name="T">The record.</typeparam>
public sealed class ZabbixEntityFrameworkStore<T> : ZabbixPersistentStore<T> where T : class
{
    private const int MaxFolded = 63 - 10 - 1 - 8;                               // zbx_store_ + folded + _ + hash, in 63 bytes

    private readonly DbContextOptions<RecordContext<T>> _options;
    private readonly string _table;

    /// <summary>A store on the database <paramref name="configureDatabase"/> describes, e.g. <c>o =&gt; o.UseNpgsql(connectionString)</c>.</summary>
    /// <param name="configureDatabase">EF's native options builder.</param>
    /// <param name="policy">The policy.</param>
    /// <param name="services">The store's name (its table), logging, meters and clock.</param>
    public ZabbixEntityFrameworkStore(Action<DbContextOptionsBuilder> configureDatabase, ZabbixStorePolicy<T>? policy = null, ZabbixStoreServices? services = null)
        : base(policy, services)
    {
        ArgumentNullException.ThrowIfNull(configureDatabase);
        var builder = new DbContextOptionsBuilder<RecordContext<T>>();
        configureDatabase(builder);
        builder.ReplaceService<IModelCacheKeyFactory, TableModelCacheKeyFactory>();
        _options = builder.Options;
        _table = TableName(Name);
    }

    /// <summary>The table this store keeps its records in.</summary>
    public string Table => _table;

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "PostgreSQL folds unquoted identifiers to lower case.")]
    internal static string TableName(string store)
    {
        var name = new System.Text.StringBuilder("zbx_store_", 63);
        foreach (char c in store.AsSpan(0, Math.Min(store.Length, MaxFolded))) name.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(store));
        return name.Append('_').Append(Convert.ToHexStringLower(hash.AsSpan(0, 4))).ToString();
    }

    private RecordContext<T> Context() => new(_options, _table);

    /// <inheritdoc/>
    /// <remarks>
    /// <c>EnsureCreated</c> no-ops on a populated database (a shared database is one), so the table is created on its own.
    /// A provider reports "already exists" as its own <see cref="DbException"/>, and so does a refused <c>CREATE</c> (a
    /// user without the right). So a failed create is followed by a read of the bare table: a table that does not answer
    /// it was not created, which is reported with the provider's error; one that does is read once with every column the
    /// record maps, and a table that cannot answer that read is reported as one that does not match the record. A
    /// database that cannot be reached fails before, at the existence check, with the provider's own exception.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The table could not be created, or it exists and does not hold the
    /// record's columns.</exception>
    protected override async ValueTask<int> OpenAsync(CancellationToken cancellationToken)
    {
        RecordContext<T> context = Context();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable closing = context.ConfigureAwait(false);
        IRelationalDatabaseCreator creator = context.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(cancellationToken).ConfigureAwait(false)) await creator.CreateAsync(cancellationToken).ConfigureAwait(false);
        DbException? notCreated = null;
        try
        {
            await creator.CreateTablesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException failure)
        {
            notCreated = failure;
        }
        if (notCreated is not null)
        {
            if (!await TableExistsAsync(context, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException($"The table {_table} does not exist and could not be created: {notCreated.Message}", notCreated);
            try
            {
                _ = await context.Records.AsNoTracking().Take(1).ToListAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbException mismatch)
            {
                throw new InvalidOperationException($"The table {_table} exists and does not hold the columns of {typeof(T).Name}: {mismatch.Message}", mismatch);
            }
        }
        return await context.Records.CountAsync(cancellationToken).ConfigureAwait(false);
    }

    // Whether the table answers a query that names no column: the store's generated name, delimited by the provider.
    [SuppressMessage("Security", "EF1003:Possible SQL injection", Justification = "A table name cannot be a parameter; it is the store's generated name ([a-z0-9_] only, TableName), delimited by the provider's ISqlGenerationHelper.")]
    private async Task<bool> TableExistsAsync(RecordContext<T> context, CancellationToken cancellationToken)
    {
        string table = context.GetService<ISqlGenerationHelper>().DelimitIdentifier(_table);
        try
        {
            _ = await context.Database.ExecuteSqlRawAsync("SELECT 1 FROM " + table + " WHERE 1 = 0", cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override async ValueTask<IReadOnlyList<long>> AppendAsync(IReadOnlyList<ZabbixStoreEntry<T>> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        RecordContext<T> context = Context();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable closing = context.ConfigureAwait(false);
        var entries = new Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<T>[records.Count];
        for (int i = 0; i < records.Count; i++)
        {
            entries[i] = context.Records.Add(records[i].Record);
            entries[i].Property<long>(RecordContext<T>.StoredKey).CurrentValue = records[i].Stored.UtcTicks;
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return [.. entries.Select(entry => entry.Property<long>(RecordContext<T>.SeqKey).CurrentValue)];
    }

    /// <inheritdoc/>
    protected override async ValueTask<IReadOnlyList<ZabbixStoreEntry<T>>> LoadAfterAsync(long sequence, int count, CancellationToken cancellationToken)
    {
        RecordContext<T> context = Context();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable closing = context.ConfigureAwait(false);
        var rows = await context.Records.AsNoTracking()
            .Where(record => EF.Property<long>(record, RecordContext<T>.SeqKey) > sequence)
            .OrderBy(record => EF.Property<long>(record, RecordContext<T>.SeqKey))
            .Take(count)
            .Select(record => new { Record = record, Seq = EF.Property<long>(record, RecordContext<T>.SeqKey), Stored = EF.Property<long>(record, RecordContext<T>.StoredKey) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(row => new ZabbixStoreEntry<T>(row.Seq, new DateTimeOffset(row.Stored, TimeSpan.Zero), row.Record))];
    }

    /// <inheritdoc/>
    protected override async ValueTask<int> RemoveAsync(long first, long last, CancellationToken cancellationToken)
    {
        RecordContext<T> context = Context();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable closing = context.ConfigureAwait(false);
        return await context.Records
            .Where(record => EF.Property<long>(record, RecordContext<T>.SeqKey) >= first && EF.Property<long>(record, RecordContext<T>.SeqKey) <= last)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
