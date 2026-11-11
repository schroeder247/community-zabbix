// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Data.Common;
using Community.Zabbix.Storage;
using Community.Zabbix.Storage.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Community.Zabbix.Tests;

/// <summary>
/// The EF durable store on SQLite (always, a throwaway file) and PostgreSQL (when
/// <c>ZABBIX_TEST_POSTGRES</c> is set, as <c>EfStorageTests</c> does) — the lease cursor rewound over a read-ahead page,
/// restart consistency after acknowledgements, a database that is not there at first use, a file that turns read-only, a
/// value the column cannot hold, and many writers against one reader.
/// </summary>
[Collection("Adversarial hosts stress")]
public sealed class AdversarialStoreEfTests
{
    // Bounds a hang, not a commit: four writers contending for SQLite can take tens of seconds on a Windows runner.
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(60);

    public static TheoryData<string> Providers => new("sqlite", "postgres");

    /// <summary>One database for one test; the tables it created are dropped (PostgreSQL) or the file deleted (SQLite).</summary>
    private sealed class Database : IAsyncDisposable
    {
        private readonly List<string> _tables = [];

        private Database(string provider, string connection, string? directory)
        {
            Provider = provider;
            Connection = connection;
            Directory = directory;
        }

        public string Provider { get; }

        public string Connection { get; }

        /// <summary>The SQLite file's own directory; <see langword="null"/> on PostgreSQL.</summary>
        public string? Directory { get; }

        public string? File => Directory is null ? null : Path.Combine(Directory, "store.db");

        public static async Task<Database> OpenAsync(string provider, bool createDirectory = true)
        {
            if (provider == "sqlite")
            {
                string directory = Path.Combine(Path.GetTempPath(), $"zbx-adv-{Guid.NewGuid():N}");
                if (createDirectory) System.IO.Directory.CreateDirectory(directory);
                return new Database(provider, $"Data Source={Path.Combine(directory, "store.db")};Pooling=False", directory);
            }
            string? connection = Lab.Postgres;
            if (string.IsNullOrWhiteSpace(connection))
                Assert.Skip("ZABBIX_TEST_POSTGRES is not set; point it at a PostgreSQL database the tests own (see EfStorageTests)");
            try
            {
                await using var probe = new NpgsqlConnection(connection);
                await probe.OpenAsync(TestContext.Current.CancellationToken);
            }
            catch (Exception failure) when (failure is NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
            {
                Assert.Skip($"the database in ZABBIX_TEST_POSTGRES is not reachable ({failure.GetType().Name}); start it or fix the connection string");
            }
            return new Database(provider, connection, null);
        }

        public ZabbixEntityFrameworkStore<T> Store<T>(string name, ZabbixStorePolicy<T>? policy = null) where T : class
        {
            var services = new ZabbixStoreServices { Name = name };
            ZabbixEntityFrameworkStore<T> store = Provider == "sqlite"
                ? new ZabbixEntityFrameworkStore<T>(options => options.UseSqlite(Connection), policy, services)
                : new ZabbixEntityFrameworkStore<T>(options => options.UseNpgsql(Connection), policy, services);
            if (!_tables.Contains(store.Table)) _tables.Add(store.Table);
            return store;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The identifier is ZabbixEntityFrameworkStore's generated table name, validated against [a-z0-9_].")]
        public async ValueTask DisposeAsync()
        {
            if (Directory is not null)
            {
                SqliteConnection.ClearAllPools();
                if (File is { } file && System.IO.File.Exists(file)) System.IO.File.SetAttributes(file, FileAttributes.Normal);
                if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
                return;
            }
            await using var connection = new NpgsqlConnection(Connection);
            await connection.OpenAsync();
            foreach (string table in _tables)
            {
                Assert.Matches("^[a-z0-9_]+$", table);
                await using DbCommand drop = connection.CreateCommand();
                drop.CommandText = $"DROP TABLE IF EXISTS {table}";
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static string UniqueName() => "adv_" + Guid.NewGuid().ToString("N")[..12];

    private static ZabbixValue Value(int i, string? value = null)
        => new("web01", $"cpu[{i}]", value ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.UnixEpoch.AddSeconds(i));

    private static List<ZabbixStoreEntry<ZabbixValue>> Lease(ZabbixStore<ZabbixValue> store, int count)
    {
        var leased = new List<ZabbixStoreEntry<ZabbixValue>>();
        while (leased.Count < count && store.TryLease(out ZabbixStoreEntry<ZabbixValue> entry)) leased.Add(entry);
        return leased;
    }

    // A store opened by this process (a restart) has nothing in memory until its loop has opened the table and loaded
    // ahead of the cursor: the peek asks for that, the flush waits for it.
    private static async Task<List<ZabbixStoreEntry<ZabbixValue>>> LeaseAllAfterOpenAsync(ZabbixEntityFrameworkStore<ZabbixValue> store)
    {
        store.TryPeek(out _);
        await store.FlushAsync();
        return LeaseAll(store);
    }

    private static List<ZabbixStoreEntry<ZabbixValue>> LeaseAll(ZabbixStore<ZabbixValue> store)
    {
        var leased = new List<ZabbixStoreEntry<ZabbixValue>>();
        while (store.TryLease(out ZabbixStoreEntry<ZabbixValue> entry)) leased.Add(entry);
        return leased;
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Releasing_a_leased_page_hands_out_its_first_record_again_not_the_last(string provider)
    {
        await using Database database = await Database.OpenAsync(provider);
        await using ZabbixEntityFrameworkStore<ZabbixValue> store = database.Store<ZabbixValue>(UniqueName());
        for (int i = 1; i <= 5; i++) await store.WriteAsync(Value(i), TestContext.Current.CancellationToken);
        List<ZabbixStoreEntry<ZabbixValue>> page = Lease(store, 3);                       // a page of three; two more are read ahead

        store.Release(0);                                                                 // the page did not go: all three back

        Assert.Equal(page.Select(entry => entry.Sequence), Lease(store, 3).Select(entry => entry.Sequence));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task An_acknowledgement_after_a_release_removes_only_records_handed_out_again(string provider)
    {
        await using Database database = await Database.OpenAsync(provider);
        string name = UniqueName();
        await using (ZabbixEntityFrameworkStore<ZabbixValue> store = database.Store<ZabbixValue>(name))
        {
            for (int i = 1; i <= 5; i++) await store.WriteAsync(Value(i), TestContext.Current.CancellationToken);
            Lease(store, 3);
            store.Release(0);
            Assert.True(store.TryLease(out ZabbixStoreEntry<ZabbixValue> again));

            store.Acknowledge(again.Sequence);                                            // the destination took what it was handed
        }

        await using ZabbixEntityFrameworkStore<ZabbixValue> restarted = database.Store<ZabbixValue>(name);
        Assert.Equal(["cpu[2]", "cpu[3]", "cpu[4]", "cpu[5]"], (await LeaseAllAfterOpenAsync(restarted)).Select(entry => entry.Record.Key.ToString()));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task After_a_restart_an_acknowledged_record_is_never_leased_again_and_an_unacknowledged_one_is_leased_once(string provider)
    {
        await using Database database = await Database.OpenAsync(provider);
        string name = UniqueName();
        long[] sent;
        await using (ZabbixEntityFrameworkStore<ZabbixValue> first = database.Store<ZabbixValue>(name))
        {
            for (int i = 1; i <= 5; i++) await first.WriteAsync(Value(i), TestContext.Current.CancellationToken);
            sent = [.. LeaseAll(first).Select(entry => entry.Sequence)];
            first.Acknowledge(sent[2]);
            first.Acknowledge(sent[1]);                                                   // late, out of order: already gone
        }
        await using (ZabbixEntityFrameworkStore<ZabbixValue> second = database.Store<ZabbixValue>(name))
        {
            List<ZabbixStoreEntry<ZabbixValue>> resent = await LeaseAllAfterOpenAsync(second);
            Assert.Equal(sent[3..], resent.Select(entry => entry.Sequence));
            second.Acknowledge(resent[^1].Sequence);
        }

        await using ZabbixEntityFrameworkStore<ZabbixValue> third = database.Store<ZabbixValue>(name);
        Assert.Empty(await LeaseAllAfterOpenAsync(third));
        Assert.Equal(0, third.Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_one_character_value_and_a_one_mebibyte_value_round_trip_exactly(string provider)
    {
        await using Database database = await Database.OpenAsync(provider);
        await using ZabbixEntityFrameworkStore<ZabbixValue> store = database.Store<ZabbixValue>(UniqueName());
        string large = new('x', 1 << 20);

        await store.WriteAsync(Value(1, "a"), TestContext.Current.CancellationToken);
        await store.WriteAsync(Value(2, large), TestContext.Current.CancellationToken);
        store.Complete();

        List<ZabbixValue> got = await store.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal("a", got[0].Value);
        Assert.Equal(large, got[1].Value);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_table_of_another_record_s_shape_under_the_store_s_name_fails_the_first_write_and_names_the_mismatch(string provider)
    {
        await using Database database = await Database.OpenAsync(provider);
        string name = UniqueName();
        await using (ZabbixEntityFrameworkStore<Zabbix.Proxy.ZabbixProxyAvailabilityItem> other = database.Store<Zabbix.Proxy.ZabbixProxyAvailabilityItem>(name))
            await other.WriteAsync(new Zabbix.Proxy.ZabbixProxyAvailabilityItem(7, 1), TestContext.Current.CancellationToken);   // an older schema, or another record, holds the table
        await using ZabbixEntityFrameworkStore<ZabbixValue> store = database.Store<ZabbixValue>(name);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(Value(1), TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("does not hold the columns of ZabbixValue", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task A_database_missing_at_first_use_is_retried_when_it_appears()
    {
        await using Database database = await Database.OpenAsync("sqlite", createDirectory: false);
        await using ZabbixEntityFrameworkStore<ZabbixValue> store = database.Store<ZabbixValue>(UniqueName());
        await Assert.ThrowsAnyAsync<DbException>(() => store.WriteAsync(Value(1), TestContext.Current.CancellationToken).AsTask());   // the volume is not mounted yet
        Directory.CreateDirectory(database.Directory!);

        await store.WriteAsync(Value(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task A_file_that_turns_read_only_fails_the_write_and_the_store_takes_writes_again_once_it_is_writable()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("a Unix file mode is how this test makes the file read-only");
            return;
        }
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "root writes to a file whatever its mode, so no mode makes the file read-only (a container's default user is root)");
        await using Database database = await Database.OpenAsync("sqlite");
        await using ZabbixEntityFrameworkStore<ZabbixValue> store = database.Store<ZabbixValue>(UniqueName());
        await store.WriteAsync(Value(1), TestContext.Current.CancellationToken);
        File.SetUnixFileMode(database.File!, UnixFileMode.UserRead);
        Exception? refused = await Record.ExceptionAsync(() => store.WriteAsync(Value(2), TestContext.Current.CancellationToken).AsTask());
        foreach (string file in Directory.EnumerateFiles(database.Directory!))                // the operator fixes the volume (SQLite made -shm read-only too)
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await store.WriteAsync(Value(3), TestContext.Current.CancellationToken);

        Assert.IsType<SqliteException>(Assert.IsType<DbUpdateException>(refused).InnerException);
        Assert.Equal(2, store.Count);
        store.Complete();
        Assert.Equal(["cpu[1]", "cpu[3]"], (await store.ToListAsync(TestContext.Current.CancellationToken)).Select(value => value.Key.ToString()));
    }

    [Fact]
    public async Task A_value_postgres_cannot_hold_fails_only_its_own_write_and_the_store_goes_on()
    {
        await using Database database = await Database.OpenAsync("postgres");
        await using ZabbixEntityFrameworkStore<ZabbixValue> store = database.Store<ZabbixValue>(UniqueName());
        await store.WriteAsync(Value(1), TestContext.Current.CancellationToken);
        Exception? refused = await Record.ExceptionAsync(() => store.WriteAsync(Value(2, "nul\0byte"), TestContext.Current.CancellationToken).AsTask());   // text cannot hold U+0000

        await store.WriteAsync(Value(3), TestContext.Current.CancellationToken);

        Assert.NotNull(refused);
        Assert.Equal(2, store.Count);
    }

    /// <summary>A record that carries its own <c>Id</c>: EF's convention would make it the key; the store's own sequence is.</summary>
    internal sealed record Keyed(int Id, string Name);

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Two_records_with_the_same_own_id_are_both_kept_because_the_sequence_is_the_key(string provider)
    {
        await using Database database = await Database.OpenAsync(provider);
        await using ZabbixEntityFrameworkStore<Keyed> store = database.Store<Keyed>(UniqueName());

        await store.WriteAsync(new Keyed(1, "first"), TestContext.Current.CancellationToken);
        await store.WriteAsync(new Keyed(1, "second"), TestContext.Current.CancellationToken);
        store.Complete();

        Assert.Equal(["first", "second"], (await store.ToListAsync(TestContext.Current.CancellationToken)).Select(record => record.Name));
    }

    /// <summary>Fails each <c>DELETE</c> the store runs while armed, as a database that drops the connection mid-statement does.</summary>
    private sealed class FailDeletes : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private int _failures;

        public bool Armed { get; set; }

        public int Failures => Volatile.Read(ref _failures);

        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> NonQueryExecuting(DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result)
        {
            Fail(command);
            return result;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return ValueTask.FromResult(result);
        }

        private void Fail(DbCommand command)
        {
            if (Armed && command.CommandText.Contains("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _failures);
                throw new SqliteException("disk I/O error", 10);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The identifier is ZabbixEntityFrameworkStore's generated table name, validated against [a-z0-9_].")]
    private static async Task<long> RowsAsync(string connection, string table)
    {
        Assert.Matches("^[a-z0-9_]+$", table);
        await using var sqlite = new SqliteConnection(connection);
        await sqlite.OpenAsync(TestContext.Current.CancellationToken);
        await using DbCommand count = sqlite.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task An_acknowledgement_the_database_failed_can_be_repeated_and_the_records_then_leave_for_good()
    {
        await using Database database = await Database.OpenAsync("sqlite");
        var failing = new FailDeletes();
        string name = UniqueName();
        await using (var store = new ZabbixEntityFrameworkStore<ZabbixValue>(options => options.UseSqlite(database.Connection).AddInterceptors(failing), services: new ZabbixStoreServices { Name = name }))
        {
            for (int i = 1; i <= 3; i++) await store.WriteAsync(Value(i), TestContext.Current.CancellationToken);
            List<ZabbixStoreEntry<ZabbixValue>> page = LeaseAll(store);
            failing.Armed = true;
            store.Acknowledge(page[^1].Sequence);                                               // out of the count at once
            await store.FlushAsync();                                                           // a whole turn whose DELETE failed: kept
            Assert.True(failing.Failures > 0);
            Assert.Equal(3, await RowsAsync(database.Connection, store.Table));

            failing.Armed = false;
            await store.FlushAsync();                                                           // the next turn removes them

            Assert.Equal(0, store.Count);
            Assert.Equal(0, await RowsAsync(database.Connection, store.Table));
        }
        await using ZabbixEntityFrameworkStore<ZabbixValue> restarted = database.Store<ZabbixValue>(name);
        Assert.Empty(await LeaseAllAfterOpenAsync(restarted));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Four_writers_and_one_lease_reader_on_the_database_lose_and_duplicate_nothing(string provider)
    {
        await using Database database = await Database.OpenAsync(provider);
        await using ZabbixEntityFrameworkStore<ZabbixValue> store = database.Store<ZabbixValue>(UniqueName());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Task<List<string>> reading = Task.Run(async () =>
        {
            var taken = new List<string>();
            var page = new List<ZabbixStoreEntry<ZabbixValue>>();
            while (await store.WaitToLeaseAsync(cancellationToken))
            {
                while (page.Count < 10 && store.TryLease(out ZabbixStoreEntry<ZabbixValue> entry)) page.Add(entry);
                if (page.Count == 0) continue;
                store.Acknowledge(page[^1].Sequence);
                taken.AddRange(page.Select(entry => entry.Record.Value));
                page.Clear();
            }
            return taken;
        }, cancellationToken);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(writer => Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++) await store.WriteAsync(Value(i, $"{writer}:{i}"), cancellationToken);
        }, cancellationToken))).WaitAsync(Failsafe, cancellationToken);
        store.Complete();
        List<string> got = await reading.WaitAsync(Failsafe, cancellationToken);

        Assert.Equal(200, got.Count);
        Assert.Equal(200, got.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(0, store.Count);
    }
}
