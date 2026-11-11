// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Community.Zabbix.Storage.EntityFrameworkCore;

/// <summary>
/// The context for one store: EF maps <typeparamref name="T"/>'s own properties to columns, so the table's schema is always
/// the record's — never a blob — and the table is the store's own (<c>zbx_store_{name}</c>), so two stores of one record
/// type never share rows. A shadow identity key (<c>__seq</c>) keeps arrival order and is the entry's sequence, and a
/// shadow <c>__stored</c> keeps when it arrived, without a field on the record; a
/// <see cref="ZabbixItemKey"/> column is its text, a <see cref="DateTimeOffset"/> column is its UTC ticks (one ordered
/// <c>bigint</c> on every provider) and a <see cref="ulong"/> column is a <c>bigint</c> as Zabbix's own tables keep ids
/// (the unsigned number's 64 bits, unchanged). A <see cref="ZabbixValue"/> keeps its exact instant, to the nanosecond, as
/// nanoseconds since the epoch: history's key is <c>(itemid, clock, ns)</c>, so a relayed value's time must not round.
/// </summary>
internal sealed class RecordContext<T>(DbContextOptions<RecordContext<T>> options, string table) : DbContext(options), IRecordTable
    where T : class
{
    public const string SeqKey = "__seq";

    public const string StoredKey = "__stored";

    /// <summary>The table this context maps the record to; part of the model's cache key.</summary>
    public string Table { get; } = table;

    public DbSet<T> Records => Set<T>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<ZabbixItemKey>().HaveConversion<ZabbixItemKeyConverter>();
        configurationBuilder.Properties<ZabbixClock>().HaveConversion<ZabbixClockConverter>();
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<ulong>().HaveConversion<ZabbixIdConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity = modelBuilder.Entity<T>();
        entity.ToTable(Table);
        entity.Property<long>(SeqKey).ValueGeneratedOnAdd();
        entity.HasKey(SeqKey);
        entity.Property<long>(StoredKey);
        if (typeof(T) == typeof(ZabbixValue))
        {
            entity.Ignore(nameof(ZabbixValue.Time));
            entity.Property<ZabbixClock>(nameof(ZabbixValue.Clock));
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "EF Core instantiates the converter from HaveConversion<TConverter>() in ConfigureConventions.")]
    private sealed class ZabbixItemKeyConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<ZabbixItemKey, string>(
        key => key.ToString(), text => ZabbixItemKey.Parse(text, CultureInfo.InvariantCulture));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "EF Core instantiates the converter from HaveConversion<TConverter>() in ConfigureConventions.")]
    private sealed class ZabbixClockConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<ZabbixClock, long>(
        clock => clock.ToUnixNanoseconds(), nanoseconds => ZabbixClock.FromUnixNanoseconds(nanoseconds));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "EF Core instantiates the converter from HaveConversion<TConverter>() in ConfigureConventions.")]
    private sealed class UtcTicksConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
        time => time.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "EF Core instantiates the converter from the convention.")]
    private sealed class ZabbixIdConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<ulong, long>(
        id => unchecked((long)id), number => unchecked((ulong)number));
}
