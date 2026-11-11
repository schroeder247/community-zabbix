// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Text;

namespace Community.Zabbix;

/// <summary>
/// Represents the <c>filter</c> or <c>search</c> of a <c>.get</c>: the generated types name each field the API takes, and the
/// indexer sets a field they don't name, by its wire name. Only what is set is sent.
/// </summary>
/// <example>
/// <code language="csharp">
/// var hosts = await zabbix.Hosts.GetAsync(q =>
/// {
///     q.Filter.Status = [ZabbixHostStatus.Enabled];
///     q.Search.VisibleName = "web";
///     q.Search["a_field_of_a_newer_zabbix"] = "value";
/// });
/// </code>
/// </example>
public abstract record ZabbixGetConditions : IReadOnlyDictionary<string, ZabbixFieldValue>
{
    private Dictionary<string, ZabbixFieldValue>? _fields;

    /// <summary>Initializes conditions with nothing set.</summary>
    protected ZabbixGetConditions()
    {
    }

    /// <summary>Initializes conditions with fields set by their wire names.</summary>
    /// <param name="fields">The wire names and values; none when <see langword="null"/>.</param>
    protected ZabbixGetConditions(IEnumerable<KeyValuePair<string, ZabbixFieldValue>>? fields)
    {
        if (fields is null) return;
        foreach (KeyValuePair<string, ZabbixFieldValue> field in fields) this[field.Key] = field.Value;
    }

    /// <summary>Initializes a copy of <paramref name="original"/>, for <c>with</c>.</summary>
    /// <param name="original">The conditions to copy.</param>
    protected ZabbixGetConditions(ZabbixGetConditions original)
    {
        ArgumentNullException.ThrowIfNull(original);
        _fields = original._fields is null ? null : new(original._fields, StringComparer.Ordinal);
    }

    /// <summary>Gets or sets the condition on a field, by its wire name.</summary>
    /// <param name="key">The field's wire name.</param>
    /// <returns>The condition.</returns>
    /// <exception cref="KeyNotFoundException">Reading a field that isn't set.</exception>
    public ZabbixFieldValue this[string key]
    {
        get => TryGetValue(key, out ZabbixFieldValue value) ? value : throw new KeyNotFoundException($"No condition is set on the field '{key}'.");
        set
        {
            ArgumentNullException.ThrowIfNull(key);
            (_fields ??= new(StringComparer.Ordinal))[key] = value;
        }
    }

    /// <inheritdoc/>
    public int Count => Entries().Count();

    /// <inheritdoc/>
    public IEnumerable<string> Keys => Entries().Select(static entry => entry.Key);

    /// <inheritdoc/>
    public IEnumerable<ZabbixFieldValue> Values => Entries().Select(static entry => entry.Value);

    /// <inheritdoc/>
    public bool ContainsKey(string key) => TryGetValue(key, out _);

    /// <inheritdoc/>
    public bool TryGetValue(string key, out ZabbixFieldValue value)
    {
        foreach (KeyValuePair<string, ZabbixFieldValue> entry in Entries())
        {
            if (entry.Key == key)
            {
                value = entry.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, ZabbixFieldValue>> GetEnumerator() => Entries().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Gets whether <paramref name="other"/> sets the same fields to the same values.</summary>
    /// <param name="other">The conditions to compare with these.</param>
    /// <returns><see langword="true"/> when both send the same fields with the same values.</returns>
    public virtual bool Equals(ZabbixGetConditions? other) => other is not null && ZabbixValueEquality.DictionaryEqual<ZabbixFieldValue>(this, other);

    /// <inheritdoc/>
    public override int GetHashCode() => Count;

    /// <summary>Writes what is set, for the record's text form.</summary>
    /// <param name="builder">Where to write it.</param>
    /// <returns>Whether anything was written.</returns>
    protected virtual bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        bool more = false;
        foreach (KeyValuePair<string, ZabbixFieldValue> entry in Entries()) ZabbixRecordText.Member(builder, ref more, entry.Key, entry.Value);
        return more;
    }

    /// <summary>Gets the conditions the type's own members hold.</summary>
    /// <returns>Their wire names and values.</returns>
    protected abstract IEnumerable<KeyValuePair<string, ZabbixFieldValue>> MemberConditions();

    // A field set by its wire name wins over a member of the same wire name: it is the later, more explicit word.
    private IEnumerable<KeyValuePair<string, ZabbixFieldValue>> Entries()
    {
        foreach (KeyValuePair<string, ZabbixFieldValue> entry in MemberConditions())
        {
            if (_fields is null || !_fields.ContainsKey(entry.Key)) yield return entry;
        }
        if (_fields is null) yield break;
        foreach (KeyValuePair<string, ZabbixFieldValue> entry in _fields) yield return entry;
    }
}
