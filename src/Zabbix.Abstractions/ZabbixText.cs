// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text;

namespace Community.Zabbix;

/// <summary>
/// Text that goes on the wire as UTF-8 JSON. A lone UTF-16 surrogate has no UTF-8 form: System.Text.Json's writer puts
/// U+FFFD in its place without a word (on .NET 10, the default and the relaxed encoder alike: <c>"a\uD800b"</c>
/// is written <c>"a\uFFFDb"</c>), so the caller's data would change silently. Sending it escaped (<c>\uD800</c>) is no
/// better: PHP's <c>json_decode</c> refuses an unpaired surrogate escape (<c>JSON_ERROR_UTF16</c>), so the whole request
/// would fail at the server as a parse error. It is refused here, as the caller's argument.
/// </summary>
internal static class ZabbixText
{
    /// <summary>Throws when <paramref name="text"/> holds a surrogate that is not half of a pair.</summary>
    /// <param name="text">The text.</param>
    /// <param name="paramName">The argument it came from.</param>
    /// <exception cref="ArgumentException">It does.</exception>
    public static void ThrowIfLoneSurrogate(ReadOnlySpan<char> text, string? paramName)
    {
        int at = text.IndexOfAnyInRange('\uD800', '\uDFFF');                // vectorized: text without surrogates ends here
        while (at >= 0)
        {
            if (Rune.DecodeFromUtf16(text[at..], out _, out int consumed) != System.Buffers.OperationStatus.Done)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                    $"The text holds a lone UTF-16 surrogate (U+{(int)text[at]:X4} at index {at}), which UTF-8 JSON cannot carry."), paramName);
            }
            int next = text[(at + consumed)..].IndexOfAnyInRange('\uD800', '\uDFFF');
            at = next < 0 ? -1 : at + consumed + next;
        }
    }
}
