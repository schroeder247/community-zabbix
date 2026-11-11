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
/// The durable store on EF Core, run on two providers: SQLite (a throwaway file per test, always) and PostgreSQL (a
/// database the tests own, <c>ZABBIX_TEST_POSTGRES</c>; unset or unreachable is a skip with the reason and the remedy).
/// The record's properties are the columns, each store has its own table, records round-trip in order, and a leased
/// record stays in the table until it is acknowledged. A test never touches a Zabbix database.
/// </summary>
public sealed class EfStorageTests
{
    public static TheoryData<string> Providers => new("sqlite", "postgres");

    /// <summary>One database for one test: how to point a store at it, how to read its catalogue, how to clean up.</summary>
    private sealed class Database : IAsyncDisposable
    {
        private readonly string? _file;
        private readonly List<string> _tables = [];

        private Database(string provider, string connection, string? file)
        {
            Provider = provider;
            Connection = connection;
            _file = file;
        }

        public string Provider { get; }

        public string Connection { get; }

        public static async Task<Database> OpenAsync(string provider)
        {
            if (provider == "sqlite")
            {
                string file = Path.Combine(Path.GetTempPath(), $"zbx-store-{Guid.NewGuid():N}.db");
                return new Database(provider, $"Data Source={file};Pooling=False", file);
            }
            string? connection = Lab.Postgres;
            if (string.IsNullOrWhiteSpace(connection))
                Assert.Skip("ZABBIX_TEST_POSTGRES is not set; point it at a PostgreSQL database the tests own, e.g. `podman run -d -p 55432:5432 -e POSTGRES_USER=zbxtest -e POSTGRES_PASSWORD=zbxtest -e POSTGRES_DB=zbxtest postgres:16-alpine` and Host=127.0.0.1;Port=55432;Database=zbxtest;UserName=zbxtest;Password=zbxtest");
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

        public ZabbixEntityFrameworkStore<ZabbixValue> Store(string name)
        {
            var services = new ZabbixStoreServices { Name = name };
            ZabbixEntityFrameworkStore<ZabbixValue> store = Provider == "sqlite"
                ? new ZabbixEntityFrameworkStore<ZabbixValue>(options => options.UseSqlite(Connection), services: services)
                : new ZabbixEntityFrameworkStore<ZabbixValue>(options => options.UseNpgsql(Connection), services: services);
            if (!_tables.Contains(store.Table)) _tables.Add(store.Table);
            return store;
        }

        private DbConnection Open() => Provider == "sqlite" ? new SqliteConnection(Connection) : new NpgsqlConnection(Connection);

        // A table name cannot be a parameter; it is the store's generated name, [a-z0-9_] only, and checked here.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The identifier is ZabbixEntityFrameworkStore's generated table name, validated against [a-z0-9_].")]
        public async Task<List<string>> ColumnsAsync(string table)
        {
            Assert.Matches("^[a-z0-9_]+$", table);
            await using DbConnection connection = Open();
            await connection.OpenAsync();
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = Provider == "sqlite"
                ? $"SELECT name FROM pragma_table_info('{table}')"
                : $"SELECT column_name FROM information_schema.columns WHERE table_name = '{table}'";
            var names = new List<string>();
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            return names;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The identifier is ZabbixEntityFrameworkStore's generated table name, validated against [a-z0-9_].")]
        public async ValueTask DisposeAsync()
        {
            if (_file is not null)
            {
                SqliteConnection.ClearAllPools();
                File.Delete(_file);
                return;
            }
            await using DbConnection connection = Open();
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

    private static string UniqueName() => "test_" + Guid.NewGuid().ToString("N")[..12];

    private static ZabbixValue Value(int i) => new("web01", $"cpu[{i}]", i.ToString(System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.UnixEpoch.AddSeconds(i));

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task The_columns_are_the_records_properties_in_the_store_s_own_table(string provider)
    {
        await using Database database = await Database.OpenAsync(provider);
        await using ZabbixEntityFrameworkStore<ZabbixValue> store = database.Store(UniqueName());

        await store.WriteAsync(Value(1), TestContext.Current.CancellationToken);                                                 // the table appears at first use

        Assert.Equal(["Clock", "HostName", "IsSupported", "Key", "Value", "__seq", "__stored"], (await database.ColumnsAsync(store.Table)).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_not_supported_value_keeps_its_state_through_the_store(string provider)
    {
        await using Database database = await Database.OpenAsync(provider);
        await using ZabbixEntityFrameworkStore<ZabbixValue> store = database.Store(UniqueName());
        await store.WriteAsync(Value(1) with { Value = "Unsupported item key.", IsSupported = false }, TestContext.Current.CancellationToken);
        store.Complete();

        ZabbixValue got = Assert.Single(await store.ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal(("Unsupported item key.", false), (got.Value, got.IsSupported));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Records_round_trip_through_the_typed_columns_in_order(string provider)
    {
        await using Database database = await Database.OpenAsync(provider);
        await using ZabbixEntityFrameworkStore<ZabbixValue> store = database.Store(UniqueName());
        for (int i = 0; i < 3; i++) await store.WriteAsync(Value(i), TestContext.Current.CancellationToken);
        store.Complete();

        List<ZabbixValue> got = await store.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["cpu[0]", "cpu[1]", "cpu[2]"], got.Select(value => value.Key.ToString()));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(2), got[2].Clock);
        Assert.Equal(0, store.Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Two_stores_of_one_record_type_do_not_share_rows(string provider)
    {
        await using Database database = await Database.OpenAsync(provider);
        await using ZabbixEntityFrameworkStore<ZabbixValue> a = database.Store(UniqueName());
        await using ZabbixEntityFrameworkStore<ZabbixValue> b = database.Store(UniqueName());
        await a.WriteAsync(new ZabbixValue("h", "a", "1", DateTimeOffset.UnixEpoch), TestContext.Current.CancellationToken);
        await b.WriteAsync(new ZabbixValue("h", "b", "2", DateTimeOffset.UnixEpoch), TestContext.Current.CancellationToken);
        a.Complete();
        b.Complete();

        Assert.Equal(["a"], (await a.ToListAsync(TestContext.Current.CancellationToken)).Select(v => v.Key.ToString()));
        Assert.Equal(["b"], (await b.ToListAsync(TestContext.Current.CancellationToken)).Select(v => v.Key.ToString()));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_batch_in_flight_when_the_process_stops_is_in_the_table_after_the_restart_with_the_same_sequences(string provider)
    {
        await using Database database = await Database.OpenAsync(provider);
        string name = UniqueName();
        long inFlight;
        await using (ZabbixEntityFrameworkStore<ZabbixValue> first = database.Store(name))
        {
            for (int i = 0; i < 3; i++) await first.WriteAsync(Value(i), TestContext.Current.CancellationToken);
            Assert.True(first.TryLease(out ZabbixStoreEntry<ZabbixValue> a));
            Assert.True(first.TryLease(out ZabbixStoreEntry<ZabbixValue> b));
            first.Acknowledge(a.Sequence);                                                // the server took a; b is in flight at the "crash"
            inFlight = b.Sequence;
        }

        await using ZabbixEntityFrameworkStore<ZabbixValue> restarted = database.Store(name);
        Assert.True(await restarted.WaitToLeaseAsync(TestContext.Current.CancellationToken));   // opened, and loaded ahead of the cursor
        var leased = new List<ZabbixStoreEntry<ZabbixValue>>();
        while (restarted.TryLease(out ZabbixStoreEntry<ZabbixValue> entry)) leased.Add(entry);

        Assert.Equal(["cpu[1]", "cpu[2]"], leased.Select(entry => entry.Record.Key.ToString()));
        Assert.Equal(inFlight, leased[0].Sequence);
        Assert.Equal(2, restarted.Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Sequences_continue_above_every_id_after_the_table_empties_and_the_process_restarts(string provider)
    {
        await using Database database = await Database.OpenAsync(provider);
        string name = UniqueName();
        long highest = 0;
        await using (ZabbixEntityFrameworkStore<ZabbixValue> first = database.Store(name))
        {
            await first.WriteAsync(Value(1), TestContext.Current.CancellationToken);
            await first.WriteAsync(Value(2), TestContext.Current.CancellationToken);
            while (first.TryLease(out ZabbixStoreEntry<ZabbixValue> entry)) highest = entry.Sequence;
            first.Acknowledge(highest);                                                   // the table is empty
            Assert.Equal(0, first.Count);
        }

        await using ZabbixEntityFrameworkStore<ZabbixValue> restarted = database.Store(name);
        await restarted.WriteAsync(Value(3), TestContext.Current.CancellationToken);
        Assert.True(restarted.TryLease(out ZabbixStoreEntry<ZabbixValue> next));

        Assert.True(next.Sequence > highest, $"sequence {next.Sequence} reused an id at or below {highest}");
    }

    [Fact]
    public async Task A_table_the_database_refuses_to_create_is_reported_as_not_created_not_as_a_mismatch()
    {
        // A user without the right to CREATE gets a DbException from CreateTables, as an existing table does; the store
        // must not then call the missing table one "that does not hold the columns". A read-only SQLite file is the
        // refusal without a server.
        string file = Path.Combine(Path.GetTempPath(), $"zbx-store-{Guid.NewGuid():N}.db");
        try
        {
            await using (var seed = new SqliteConnection($"Data Source={file};Pooling=False"))
            {
                await seed.OpenAsync(TestContext.Current.CancellationToken);
                await using SqliteCommand create = seed.CreateCommand();
                create.CommandText = "CREATE TABLE other (id INTEGER)";
                await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await using var store = new ZabbixEntityFrameworkStore<ZabbixValue>(options => options.UseSqlite($"Data Source={file};Mode=ReadOnly;Pooling=False"),
                services: new ZabbixStoreServices { Name = UniqueName() });

            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(Value(1), TestContext.Current.CancellationToken).AsTask());

            Assert.Contains("could not be created", refused.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("does not hold the columns", refused.Message, StringComparison.Ordinal);
            Assert.IsAssignableFrom<DbException>(refused.InnerException);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The identifier is ZabbixEntityFrameworkStore's generated table name, validated against [a-z0-9_].")]
    public async Task A_table_of_the_store_s_name_with_other_columns_is_reported_as_a_mismatch()
    {
        string file = Path.Combine(Path.GetTempPath(), $"zbx-store-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new ZabbixEntityFrameworkStore<ZabbixValue>(options => options.UseSqlite($"Data Source={file};Pooling=False"),
                services: new ZabbixStoreServices { Name = UniqueName() });
            Assert.Matches("^[a-z0-9_]+$", store.Table);
            await using (var seed = new SqliteConnection($"Data Source={file};Pooling=False"))
            {
                await seed.OpenAsync(TestContext.Current.CancellationToken);
                await using SqliteCommand create = seed.CreateCommand();
                create.CommandText = $"CREATE TABLE {store.Table} (id INTEGER)";
                await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(Value(1), TestContext.Current.CancellationToken).AsTask());

            Assert.Contains("does not hold the columns of ZabbixValue", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public async Task The_durable_store_takes_its_policy_and_services_as_a_memory_store_does()
    {
        // One way to build a store: the policy record and the services, handed to the store's constructor.
        string file = Path.Combine(Path.GetTempPath(), $"zbx-store-{Guid.NewGuid():N}.db");
        try
        {
            ZabbixStore<ZabbixValue> built = new ZabbixEntityFrameworkStore<ZabbixValue>(
                options => options.UseSqlite($"Data Source={file};Pooling=False"), new ZabbixStorePolicy<ZabbixValue> { MaxItems = 3 }, new ZabbixStoreServices { Name = UniqueName() });
            await using (built)
            {
                ZabbixEntityFrameworkStore<ZabbixValue> store = Assert.IsType<ZabbixEntityFrameworkStore<ZabbixValue>>(built);
                Assert.Equal(3, store.Policy.MaxItems);
                await store.WriteAsync(Value(1), TestContext.Current.CancellationToken);
                await store.WriteAsync(Value(2), TestContext.Current.CancellationToken);

                Assert.True(store.TryLease(out ZabbixStoreEntry<ZabbixValue> first));
                Assert.Equal(Value(1), first.Record);
                Assert.Equal(2, store.Count);
            }
            Assert.Throws<ArgumentNullException>(() => new ZabbixEntityFrameworkStore<ZabbixValue>(null!));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public async Task The_cached_count_stays_right_after_cumulative_removes_and_after_a_release()
    {
        // PersistRemove keeps a running _count rather than re-querying the table on every write and remove (the store
        // owns its table); this proves that cache survives two separate cumulative Acknowledge calls and a Release,
        // which touches the lease cursor only and must not move the count at all.
        await using Database database = await Database.OpenAsync("sqlite");
        await using ZabbixEntityFrameworkStore<ZabbixValue> store = database.Store(UniqueName());
        for (int i = 0; i < 5; i++) await store.WriteAsync(Value(i), TestContext.Current.CancellationToken);
        Assert.Equal(5, store.Count);

        Assert.True(store.TryLease(out ZabbixStoreEntry<ZabbixValue> a));
        Assert.True(store.TryLease(out ZabbixStoreEntry<ZabbixValue> b));
        store.Acknowledge(b.Sequence);                                                    // cumulative: a and b leave together
        Assert.Equal(3, store.Count);

        Assert.True(store.TryLease(out ZabbixStoreEntry<ZabbixValue> c));
        Assert.True(store.TryLease(out ZabbixStoreEntry<ZabbixValue> d));
        store.Acknowledge(d.Sequence);                                                    // a second cumulative remove
        Assert.Equal(1, store.Count);

        Assert.True(store.TryLease(out ZabbixStoreEntry<ZabbixValue> e));
        store.Release(0);                                                                 // gives e back; nothing is removed

        Assert.Equal(1, store.Count);                                                     // the release did not move the count
        Assert.True(store.TryLease(out ZabbixStoreEntry<ZabbixValue> again));
        Assert.Equal(e.Sequence, again.Sequence);
        store.Acknowledge(again.Sequence);
        Assert.Equal(0, store.Count);
    }
}
