// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Reads the macros the API takes in place of a value, at the start of a text, and answers how long each is, or 0 when
/// the text doesn't start with one. A user macro is <c>{$NAME}</c> or <c>{$NAME:context}</c>, its context quoted or
/// not and maybe a <c>regex:</c>; an LLD macro is <c>{#NAME}</c>; a built-in macro is one of the names the field allows,
/// such as <c>{HOST.HOST}</c>; and each of them may be wrapped in a macro function, <c>{{$NAME}.regsub("(.*)", \1)}</c>.
/// A name is upper-case letters, digits, dots and underscores (<c>CUserMacroParser.php</c>, <c>CLLDMacroParser.php</c>
/// and <c>CLLDMacroFunctionParser.php</c> 7.0 read them so).
/// </summary>
internal static class ZabbixMacroGrammar
{
    /// <summary>The length of the user macro or user macro function <paramref name="text"/> starts with, or 0.</summary>
    /// <param name="text">The text to read, from its first character.</param>
    public static int UserMacro(ReadOnlySpan<char> text) => Function(text, UserMacroOnly) is var function and > 0 ? function : UserMacroOnly(text);

    /// <summary>The length of the LLD macro or LLD macro function <paramref name="text"/> starts with, or 0.</summary>
    /// <param name="text">The text to read, from its first character.</param>
    public static int LldMacro(ReadOnlySpan<char> text) => Function(text, LldMacroOnly) is var function and > 0 ? function : LldMacroOnly(text);

    /// <summary>The length of the user or LLD macro, or the function of one, <paramref name="text"/> starts with, or 0.</summary>
    /// <param name="text">The text to read, from its first character.</param>
    public static int UserOrLldMacro(ReadOnlySpan<char> text) => UserMacro(text) is var user and > 0 ? user : LldMacro(text);

    /// <summary>The length of the built-in macro among <paramref name="names"/>, or the function of one, <paramref name="text"/> starts with, or 0.</summary>
    /// <param name="text">The text to read, from its first character.</param>
    /// <param name="names">The macros the field allows, each with its braces: <c>{HOST.HOST}</c>.</param>
    public static int BuiltInMacro(ReadOnlySpan<char> text, IReadOnlyList<string> names)
    {
        int Named(ReadOnlySpan<char> candidate)
        {
            foreach (string name in names)
                if (candidate.StartsWith(name, StringComparison.Ordinal)) return name.Length;
            return 0;
        }
        return Function(text, Named) is var function and > 0 ? function : Named(text);
    }

    private delegate int Token(ReadOnlySpan<char> text);

    // {$NAME}, {$NAME:context}, {$NAME:"quoted context"}, {$NAME:regex:"^[0-9]+$"}
    private static int UserMacroOnly(ReadOnlySpan<char> text)
    {
        if (!text.StartsWith("{$", StringComparison.Ordinal)) return 0;
        int p = Name(text, 2);
        if (p == 2 || p >= text.Length) return 0;
        if (text[p] == '}') return p + 1;
        if (text[p] != ':') return 0;
        p++;
        int regex = p;
        while (regex < text.Length && text[regex] == ' ') regex++;
        if (text[regex..].StartsWith("regex:", StringComparison.Ordinal)) p = regex + "regex:".Length;
        bool quoted = false;
        bool closed = false;
        bool started = false;
        for (; p < text.Length; p++)
        {
            char c = text[p];
            if (!started)
            {
                if (c == ' ') continue;
                if (c == '}') return p + 1;
                started = true;
                quoted = c == '"';
                continue;
            }
            if (quoted && !closed)
            {
                if (c == '"' && text[p - 1] != '\\') closed = true;
                continue;
            }
            if (closed)
            {
                if (c == ' ') continue;
                return c == '}' ? p + 1 : 0;
            }
            if (c == '}') return p + 1;
        }
        return 0;
    }

    // {#NAME}
    private static int LldMacroOnly(ReadOnlySpan<char> text)
    {
        if (!text.StartsWith("{#", StringComparison.Ordinal)) return 0;
        int p = Name(text, 2);
        return p > 2 && p < text.Length && text[p] == '}' ? p + 1 : 0;
    }

    // {<macro>.function(parameters)}: a function's name is lower-case letters, digits and underscores, and a quoted
    // parameter may hold a comma or a closing parenthesis.
    private static int Function(ReadOnlySpan<char> text, Token macro)
    {
        if (text.Length < 2 || text[0] != '{') return 0;
        int inner = macro(text[1..]);
        if (inner == 0) return 0;
        int p = 1 + inner;
        if (p >= text.Length || text[p] != '.') return 0;
        int name = ++p;
        while (p < text.Length && (char.IsAsciiLetterLower(text[p]) || char.IsAsciiDigit(text[p]) || text[p] == '_')) p++;
        if (p == name || p >= text.Length || text[p] != '(') return 0;
        bool quoted = false;
        for (p++; p < text.Length; p++)
        {
            char c = text[p];
            if (quoted)
            {
                if (c == '\\' && p + 1 < text.Length) p++;
                else if (c == '"') quoted = false;
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ')')
            {
                return p + 1 < text.Length && text[p + 1] == '}' ? p + 2 : 0;
            }
        }
        return 0;
    }

    // The end of a macro's name from start: upper-case letters, digits, dots and underscores.
    private static int Name(ReadOnlySpan<char> text, int start)
    {
        int p = start;
        while (p < text.Length && (char.IsAsciiLetterUpper(text[p]) || char.IsAsciiDigit(text[p]) || text[p] is '.' or '_')) p++;
        return p;
    }
}
