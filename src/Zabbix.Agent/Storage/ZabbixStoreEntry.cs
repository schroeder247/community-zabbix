// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Storage;

/// <summary>
/// A record as a store holds it: the <paramref name="Sequence"/> the store gave it when it was admitted, when it was
/// <paramref name="Stored"/>, and the record. Sequences only grow: within one store's life, and — for a durable store —
/// across restarts, because the sequence is written with the record. It is therefore an id a protocol can carry and a
/// peer can de-duplicate on (Zabbix's <c>proxy data</c> <c>id</c>, checked against the session's <c>last_id</c>).
/// </summary>
/// <typeparam name="T">The record.</typeparam>
/// <param name="Sequence">The store's id for the record, positive and increasing in admission order.</param>
/// <param name="Stored">When the record entered the store (Zabbix's <c>write_clock</c>): the age an offline buffer trims by.</param>
/// <param name="Record">The record.</param>
public readonly record struct ZabbixStoreEntry<T>(long Sequence, DateTimeOffset Stored, T Record);
