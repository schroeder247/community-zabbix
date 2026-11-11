// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents a Zabbix item key: its text, verbatim, with a parsed view of its name and parameters.
/// </summary>
/// <remarks>
/// <para>The text is the identity: the server stores it in <c>items.key_</c> and matches with <c>strcmp</c>, so
/// <c>k["a"]</c> and <c>k[a]</c> are two keys, and equality, hashing, <see cref="ToString()"/> and the wire all use the
/// text as written. <see cref="Name"/>, <see cref="Parameters"/> and <see cref="GetParameterType"/> are the view, as
/// <c>zbx_parse_item_key</c> hands them to an agent's handler.</para>
/// <para>The grammar is upstream's, ported state for state and pinned by its golden cases
/// (<c>tests/libs/zbxsysinfo/parse_item_key.yaml</c>): a text is a key when <c>zbx_parse_key</c>
/// (<c>src/libs/zbxexpr/expr.c</c>) consumes all of it, and the parameters are what <c>zbx_get_param</c>
/// (<c>src/libs/zbxparam/param.c</c>) returns. No BCL parser knows this grammar, so the scanner is owned.</para>
/// <para>In the API's JSON a key is its text (an item's <c>key_</c>). A key a reply holds that the grammar doesn't read is
/// kept as the server wrote it, its <see cref="Name"/> the whole text.</para>
/// </remarks>
[JsonConverter(typeof(ZabbixItemKeyConverter))]
public readonly struct ZabbixItemKey : IEquatable<ZabbixItemKey>, ISpanParsable<ZabbixItemKey>, ISpanFormattable
{
    private static readonly System.Buffers.SearchValues<char> s_nameChars = System.Buffers.SearchValues.Create("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz.-_");
    private readonly string? _text;
    private readonly ParsedView? _view;                                                  // null for a bare name: the text is the name

    private ZabbixItemKey(string text, bool bare)
    {
        _text = text;
        _view = bare ? null : new ParsedView();
    }

    /// <summary>The name: everything before the <c>[</c>.</summary>
    public string Name => View?.Name ?? ToString();

    /// <summary>The parameters as upstream reads them (quotes removed, <c>\"</c> unescaped; an array's inner text); empty when the key has no brackets.</summary>
    public IReadOnlyList<string> Parameters => View?.Parameters ?? [];

    private Parts? View => _view?.Get(ToString());

    /// <summary>The parameter at <paramref name="index"/>, as <see cref="Parameters"/> reads it.</summary>
    /// <param name="index">The parameter's position, from zero.</param>
    /// <exception cref="ArgumentOutOfRangeException">The key has no such parameter.</exception>
    public string this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Parameters.Count);
            return Parameters[index];
        }
    }

    /// <summary>How the parameter at <paramref name="index"/> was written: nothing, a string, or an array.</summary>
    /// <param name="index">The parameter's position, from zero.</param>
    /// <returns>The parameter's type.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The key has no such parameter.</exception>
    public ZabbixItemKeyParameterType GetParameterType(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        IReadOnlyList<ZabbixItemKeyParameterType> types = View?.Types ?? [];
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, types.Count);
        return types[index];
    }

    /// <summary>
    /// A key from its parts, each parameter quoted exactly when upstream's <c>zbx_quote_key_param</c> would quote it (it
    /// starts with <c>"</c>, a space or <c>[</c>, or holds <c>,</c> or <c>]</c>); no parameters is the bare name.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="parameters">The parameters, unquoted.</param>
    /// <returns>The key.</returns>
    /// <exception cref="FormatException"><paramref name="name"/> is not a legal key name, or a parameter needs quotes and
    /// ends with a backslash (which upstream cannot quote either).</exception>
    public static ZabbixItemKey Create(string name, [ZabbixItemKeyData] params ReadOnlySpan<string> parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        foreach (string parameter in parameters) ArgumentNullException.ThrowIfNull(parameter, nameof(parameters));
        return TryCreate(name, parameters, out ZabbixItemKey key, out string? reason) ? key : throw new FormatException($"The parts do not make a Zabbix item key: {reason}.");
    }

    /// <summary>A key from its parts, or <see langword="false"/> when they do not make one (never throws).</summary>
    /// <param name="name">The name.</param>
    /// <param name="parameters">The parameters, unquoted.</param>
    /// <param name="result">The key, when the parts make one.</param>
    /// <returns>Whether the parts make a key.</returns>
    public static bool TryCreate(string? name, [ZabbixItemKeyData] ReadOnlySpan<string> parameters, out ZabbixItemKey result)
        => TryCreate(name, parameters, out result, out _);

    private static bool TryCreate(string? name, ReadOnlySpan<string> parameters, out ZabbixItemKey result, [NotNullWhen(false)] out string? reason)
    {
        result = default;
        if (string.IsNullOrEmpty(name)) { reason = "the name is empty"; return false; }
        int illegal = name.AsSpan().IndexOfAnyExcept(s_nameChars);
        if (illegal >= 0) { reason = At("the name holds a character other than a letter, a digit, '.', '-' or '_'", illegal); return false; }
        if (parameters.IsEmpty) return TryParse(name, out result, out reason);

        var text = new StringBuilder(name).Append('[');
        for (int i = 0; i < parameters.Length; i++)
        {
            string parameter = parameters[i] ?? string.Empty;
            if (i > 0) text.Append(',');
            bool quote = parameter.Length > 0 && (parameter[0] is '"' or ' ' or '[' || parameter.AsSpan().IndexOfAny(",]") >= 0);
            if (!quote) { text.Append(parameter); continue; }
            if (parameter.EndsWith('\\')) { reason = string.Create(CultureInfo.InvariantCulture, $"parameter {i + 1} needs quotes and ends with a backslash, which cannot be quoted"); return false; }
            text.Append('"').Append(parameter.Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
        }
        return TryParse(text.Append(']').ToString(), out result, out reason);
    }

    /// <summary>Parses an item key with its parameters: <c>vfs.fs.size[/,pfree]</c>.</summary>
    /// <param name="s">The key's text.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="s"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">
    /// <paramref name="s"/> isn't an item key. The message names the rule the text breaks and where, never the text: a key's
    /// parameters can carry a secret.
    /// </exception>
    public static ZabbixItemKey Parse([ZabbixItemKeyData] string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return TryParse(s, out ZabbixItemKey key, out string? reason) ? key : throw new FormatException($"The text is not a Zabbix item key: {reason}.");
    }

    /// <summary>Parses an item key with its parameters from a span of characters.</summary>
    /// <param name="s">The key's text.</param>
    /// <returns>The key.</returns>
    /// <exception cref="FormatException"><paramref name="s"/> isn't an item key.</exception>
    public static ZabbixItemKey Parse([ZabbixItemKeyData] ReadOnlySpan<char> s) => Parse(s.ToString());

    /// <summary>Parses an item key, and says whether the text was one.</summary>
    /// <param name="s">The key's text.</param>
    /// <param name="result">The key, when the text was one.</param>
    /// <returns>Whether the text is a key.</returns>
    public static bool TryParse([NotNullWhen(true), ZabbixItemKeyData] string? s, out ZabbixItemKey result)
    {
        if (s is null) { result = default; return false; }
        return TryParse(s, out result, out _);
    }

    /// <summary>Parses an item key from a span of characters, and says whether the text was one.</summary>
    /// <param name="s">The key's text.</param>
    /// <param name="result">The key, when the text was one.</param>
    /// <returns>Whether the text is a key.</returns>
    public static bool TryParse([ZabbixItemKeyData] ReadOnlySpan<char> s, out ZabbixItemKey result)
        => TryParse(s.ToString(), out result, out _);

    // An item key has no culture: the provider the interfaces pass is ignored, and the public forms take none.
    /// <inheritdoc/>
    static ZabbixItemKey IParsable<ZabbixItemKey>.Parse(string s, IFormatProvider? provider) => Parse(s);

    /// <inheritdoc/>
    static bool IParsable<ZabbixItemKey>.TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ZabbixItemKey result) => TryParse(s, out result);

    /// <inheritdoc/>
    static ZabbixItemKey ISpanParsable<ZabbixItemKey>.Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s);

    /// <inheritdoc/>
    static bool ISpanParsable<ZabbixItemKey>.TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out ZabbixItemKey result) => TryParse(s, out result);

    /// <summary>
    /// <c>zbx_parse_key</c> over the whole text. Parsing validates and keeps the text; the name and the parameters are read
    /// by the same scan on first access, so a key that is only compared, hashed or sent costs
    /// one small holder and no strings.
    /// </summary>
    private static bool TryParse(string s, out ZabbixItemKey result, [NotNullWhen(false)] out string? reason)
    {
        var scanner = new Scanner(s, collect: false);
        if (!scanner.Run())
        {
            result = default;
            reason = scanner.Reason ?? "the text is not a key";
            return false;
        }
        result = new ZabbixItemKey(s, bare: scanner.NameLength == s.Length);
        reason = null;
        return true;
    }

    /// <summary>The name and the parameters of a key with brackets, read from its text once, on first access.</summary>
    private sealed class ParsedView
    {
        private Parts? _parts;

        public Parts Get(string text)
        {
            if (Volatile.Read(ref _parts) is { } read) return read;
            Parts parts = Parts.Read(text);
            return Interlocked.CompareExchange(ref _parts, parts, null) ?? parts;          // a racing reader's parts are equal
        }
    }

    private sealed record Parts(string Name, IReadOnlyList<string> Parameters, IReadOnlyList<ZabbixItemKeyParameterType> Types)
    {
        public static Parts Read(string text)
        {
            var scanner = new Scanner(text, collect: true);
            bool parsed = scanner.Run();
            Debug.Assert(parsed, "The text was validated when the key was parsed.");
            return new Parts(text[..scanner.NameLength], scanner.Parameters, scanner.Types);                // boxed once, not per access
        }
    }

    /// <summary>
    /// The scanner: upstream's states, 0 between parameters, 1 inside a quoted parameter, 2 inside an unquoted one, with
    /// <c>array</c> the one level of nesting upstream allows. Each step answers <see langword="null"/> to go on, or whether
    /// the key ended well. It collects <c>zbx_get_param</c>'s view only when asked to.
    /// </summary>
    private ref struct Scanner(string text, bool collect)
    {
        private readonly string _s = text;
        private readonly StringBuilder? _value = collect ? new StringBuilder() : null;
        private readonly ImmutableArray<string>.Builder? _parameters = collect ? ImmutableArray.CreateBuilder<string>() : null;
        private readonly ImmutableArray<ZabbixItemKeyParameterType>.Builder? _types = collect ? ImmutableArray.CreateBuilder<ZabbixItemKeyParameterType>() : null;
        private ZabbixItemKeyParameterType _type;
        private int _i;
        private int _state;
        private bool _array;

        public string? Reason { get; private set; }

        public int NameLength { get; private set; }

        public readonly ImmutableArray<string> Parameters => _parameters?.ToImmutable() ?? [];

        public readonly ImmutableArray<ZabbixItemKeyParameterType> Types => _types?.ToImmutable() ?? [];

        public bool Run()
        {
            while (_i < _s.Length && IsNameChar(_s[_i])) _i++;
            NameLength = _i;
            if (_i == 0) return Fail("the name is empty");
            if (_i == _s.Length) return true;
            if (_s[_i] != '[') return Fail(At("the name is followed by a character other than '['", _i));
            for (_i++; _i < _s.Length; _i++)
            {
                bool? ended = _state switch { 0 => Between(_s[_i]), 1 => Quoted(_s[_i]), _ => Unquoted(_s[_i]) };
                if (ended is { } well) return well;
            }
            return Fail(At(_state == 1 ? "a quoted parameter is not closed" : "the parameter list is not closed", _s.Length));
        }

        private bool? Between(char c)
        {
            switch (c)
            {
                case ',':
                    if (_array) Append(',');
                    else Next();
                    return null;
                case '"':
                    _state = 1;
                    Begin(ZabbixItemKeyParameterType.String);
                    if (_array) Append('"');
                    return null;
                case '[':
                    if (_array) return Fail(At("an array is inside an array", _i));
                    _array = true;
                    Begin(ZabbixItemKeyParameterType.Array);
                    return null;
                case ']':
                    return _array ? CloseArray() : End(_i);
                case ' ':
                    return null;
                default:
                    _state = 2;
                    Begin(ZabbixItemKeyParameterType.String);
                    Append(c);
                    return null;
            }
        }

        private bool? CloseArray()
        {
            _array = false;
            SkipSpaces();                                                                 // spaces after an array's ']'
            if (Peek(']')) return End(_i + 1);
            return Peek(',') ? null : Fail(At("an array is followed by neither ',' nor ']'", _i + 1));
        }

        private bool? Quoted(char c)
        {
            if (c == '"') return CloseQuote();
            if (c == '\\' && Peek('"'))
            {
                if (_array) Append('\\');
                Append('"');
                _i++;
                return null;
            }
            Append(c);
            return null;
        }

        private bool? CloseQuote()
        {
            if (_array) Append('"');
            SkipSpaces();                                                                 // spaces after a closing quote
            if (!_array && Peek(']')) return End(_i + 1);
            if (!Peek(',') && !(_array && Peek(']'))) return Fail(At("a quoted parameter is followed by neither ',' nor ']'", _i + 1));
            _state = 0;
            return null;
        }

        private bool? Unquoted(char c)
        {
            if (c == ',' || (c == ']' && _array))
            {
                _i--;
                _state = 0;
                return null;
            }
            if (c == ']') return End(_i);
            Append(c);
            return null;
        }

        private bool End(int closing)
        {
            if (closing + 1 != _s.Length) return Fail(At("text follows the closing bracket", closing + 1));
            Next();
            return true;
        }

        private void Next()
        {
            if (_value is not null)
            {
                _parameters?.Add(_value.ToString());
                _types?.Add(_type);
                _ = _value.Clear();
            }
            _type = ZabbixItemKeyParameterType.Undefined;
        }

        private void Begin(ZabbixItemKeyParameterType type)
        {
            if (_type == ZabbixItemKeyParameterType.Undefined) _type = type;
        }

        private readonly void Append(char c) => _value?.Append(c);

        private readonly bool Peek(char c) => _i + 1 < _s.Length && _s[_i + 1] == c;

        private void SkipSpaces()
        {
            while (Peek(' ')) _i++;
        }

        private bool Fail(string reason)
        {
            Reason = reason;
            return false;
        }
    }

    internal static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_';


    // A parse failure names the rule and the position (counted from one), never the text: a key's parameters can carry a
    // credential, and an exception's message reaches every log and span unredacted.
    private static string At(string rule, int index) => string.Create(CultureInfo.InvariantCulture, $"{rule} (at character {index + 1})");

    /// <summary>The key a reply holds, kept as the server wrote it whether or not the grammar reads it.</summary>
    /// <param name="text">The server's text.</param>
    /// <returns>The key.</returns>
    internal static ZabbixItemKey FromReply(string text) => TryParse(text, out ZabbixItemKey key, out _) ? key : new ZabbixItemKey(text, bare: true);

    /// <summary>The key's text, verbatim: what the server stores and what goes on the wire.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => _text ?? string.Empty;

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        ZabbixFormat.ThrowIfNotGeneral(format, nameof(ZabbixItemKey));
        return ToString();
    }

    /// <inheritdoc/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        ZabbixFormat.ThrowIfNotGeneral(format, nameof(ZabbixItemKey));
        string text = ToString();
        if (!text.TryCopyTo(destination)) { charsWritten = 0; return false; }
        charsWritten = text.Length;
        return true;
    }

    /// <summary>Keys are equal when their texts are, ordinally: the server's own <c>strcmp</c>.</summary>
    /// <param name="other">The other key.</param>
    /// <returns>Whether the texts are the same.</returns>
    public bool Equals(ZabbixItemKey other) => string.Equals(ToString(), other.ToString(), StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ZabbixItemKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => string.GetHashCode(ToString(), StringComparison.Ordinal);

    /// <summary>Equal keys.</summary>
    /// <param name="left">The first key to compare.</param>
    /// <param name="right">The second key to compare.</param>
    /// <returns><see langword="true"/> when both keys have the same text.</returns>
    public static bool operator ==(ZabbixItemKey left, ZabbixItemKey right) => left.Equals(right);

    /// <summary>Unequal keys.</summary>
    /// <param name="left">The first key to compare.</param>
    /// <param name="right">The second key to compare.</param>
    /// <returns><see langword="true"/> when the keys' texts differ.</returns>
    public static bool operator !=(ZabbixItemKey left, ZabbixItemKey right) => !left.Equals(right);
}

/// <summary>Writes a <see cref="ZabbixItemKey"/> as its text, and reads the text a reply holds.</summary>
internal sealed class ZabbixItemKeyConverter : JsonConverter<ZabbixItemKey>
{
    /// <inheritdoc/>
    public override ZabbixItemKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? ZabbixItemKey.FromReply(reader.GetString() ?? string.Empty)
            : throw new JsonException($"An item key is text, not a {reader.TokenType}.");

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ZabbixItemKey value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
