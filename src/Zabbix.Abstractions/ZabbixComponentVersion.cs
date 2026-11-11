// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Unicode;

namespace Community.Zabbix;

/// <summary>
/// Represents a Zabbix component's version as Zabbix reads and compares it: what a proxy, an agent or a server announces
/// in its <c>version</c> tag.
/// </summary>
/// <remarks>
/// The parse is <c>zbx_get_component_version</c> (<c>src/libs/zbxversion/version.c</c>): <c>major.minor[.patch]</c>,
/// each part its leading digits (<c>atoi</c>), so <c>8.0.0rc1</c> is 8.0.0; <see cref="WithoutPatch"/> is
/// <c>zbx_get_component_version_without_patch</c>, which the protocol rules compare. The type is owned because
/// <see cref="Version.TryParse(string?, out Version?)"/> refuses <c>8.0.0rc1</c>, which an 8.0 release candidate reports.
/// One deliberate difference from <c>atoi</c>: a part with no leading digit is refused rather than read as 0, so a typo
/// in a configured version is an error at start and not a silent 0.0.0.
/// </remarks>
/// <param name="Major">The major version.</param>
/// <param name="Minor">The minor version.</param>
/// <param name="Patch">The patch (Zabbix's third group); 0 when the text has none.</param>
internal readonly record struct ZabbixComponentVersion(int Major, int Minor, int Patch)
    : ISpanParsable<ZabbixComponentVersion>, ISpanFormattable, IUtf8SpanFormattable, IComparable<ZabbixComponentVersion>, IComparable
{
    /// <summary>A version without a patch: <paramref name="major"/>.<paramref name="minor"/>.0.</summary>
    /// <param name="major">The major version.</param>
    /// <param name="minor">The minor version.</param>
    public ZabbixComponentVersion(int major, int minor)
        : this(major, minor, 0)
    {
    }

    /// <summary>This version with the patch dropped: the part upstream's protocol rules compare.</summary>
    public ZabbixComponentVersion WithoutPatch => this with { Patch = 0 };

    /// <summary>The version as a <see cref="Version"/> (the patch in <see cref="Version.Build"/>).</summary>
    /// <returns>The version as a <see cref="System.Version"/> of three numbers; a suffix is dropped.</returns>
    public Version ToVersion() => new(Major, Minor, Patch);

    /// <summary>A component version from a <see cref="Version"/>; a missing build is patch 0.</summary>
    /// <param name="version">The version.</param>
    /// <returns>The component version with the same major, minor and patch numbers.</returns>
    public static ZabbixComponentVersion FromVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new ZabbixComponentVersion(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    /// <inheritdoc/>
    public static ZabbixComponentVersion Parse(string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan(), provider);
    }

    /// <inheritdoc/>
    public static ZabbixComponentVersion Parse(ReadOnlySpan<char> s, IFormatProvider? provider)
        => TryParse(s, provider, out ZabbixComponentVersion version) ? version : throw new FormatException($"'{s}' is not a Zabbix component version (major.minor[.patch], such as 7.0.31 or 8.0.0rc1).");

    /// <inheritdoc/>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ZabbixComponentVersion result) => TryParse(s.AsSpan(), provider, out result);

    /// <inheritdoc/>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out ZabbixComponentVersion result)
    {
        result = default;
        s = s.Trim();
        int dot = s.IndexOf('.');
        if (dot < 0 || !TryLeadingDigits(s[..dot], out int major)) return false;
        ReadOnlySpan<char> rest = s[(dot + 1)..];
        int second = rest.IndexOf('.');
        if (!TryLeadingDigits(second < 0 ? rest : rest[..second], out int minor)) return false;
        int patch = second >= 0 && TryLeadingDigits(rest[(second + 1)..], out int digits) ? digits : 0;   // atoi("") is 0 upstream
        result = new ZabbixComponentVersion(major, minor, patch);
        return true;
    }

    /// <summary>The version as the protocol writes it: <c>major.minor.patch</c>.</summary>
    /// <returns>The version as <c>major.minor.patch</c>; a suffix such as <c>rc1</c> is not kept.</returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        ZabbixFormat.ThrowIfNotGeneral(format, nameof(ZabbixComponentVersion));
        return ToString();
    }

    /// <inheritdoc/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        ZabbixFormat.ThrowIfNotGeneral(format, nameof(ZabbixComponentVersion));
        return destination.TryWrite(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}", out charsWritten);
    }

    /// <inheritdoc/>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        ZabbixFormat.ThrowIfNotGeneral(format, nameof(ZabbixComponentVersion));
        return Utf8.TryWrite(utf8Destination, CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}", out bytesWritten);
    }

    /// <inheritdoc/>
    public int CompareTo(ZabbixComponentVersion other)
        => Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);

    /// <inheritdoc/>
    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        ZabbixComponentVersion other => CompareTo(other),
        _ => throw new ArgumentException("The object is not a ZabbixComponentVersion.", nameof(obj)),
    };

    /// <summary>Whether <paramref name="left"/> is older.</summary>
    /// <param name="left">The first version to compare.</param>
    /// <param name="right">The second version to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is the earlier version.</returns>
    public static bool operator <(ZabbixComponentVersion left, ZabbixComponentVersion right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left"/> is newer.</summary>
    /// <param name="left">The first version to compare.</param>
    /// <param name="right">The second version to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is the later version.</returns>
    public static bool operator >(ZabbixComponentVersion left, ZabbixComponentVersion right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left"/> is older or the same.</summary>
    /// <param name="left">The first version to compare.</param>
    /// <param name="right">The second version to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is the earlier version or the same.</returns>
    public static bool operator <=(ZabbixComponentVersion left, ZabbixComponentVersion right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left"/> is newer or the same.</summary>
    /// <param name="left">The first version to compare.</param>
    /// <param name="right">The second version to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is the later version or the same.</returns>
    public static bool operator >=(ZabbixComponentVersion left, ZabbixComponentVersion right) => left.CompareTo(right) >= 0;

    // atoi: the leading digits, at least one.
    private static bool TryLeadingDigits(ReadOnlySpan<char> part, out int value)
    {
        int length = 0;
        while (length < part.Length && char.IsAsciiDigit(part[length])) length++;
        return int.TryParse(part[..length], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
