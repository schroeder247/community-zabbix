// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Community.Zabbix.Items;

/// <summary>
/// The keys of one bundled family and how each is answered: what a family provider lists and dispatches to. An answer
/// that cannot be given (<see cref="ZabbixItemException"/>, or the operating system refusing a file, a socket or a
/// platform API) is the key's not-supported reason; anything else is a fault, which the agent's table logs.
/// </summary>
internal sealed class ZabbixItemSet
{
    private static readonly TimeSpan s_patternTimeout = TimeSpan.FromSeconds(1);

    private readonly List<ZabbixItemDefinition> _items = [];
    private readonly Dictionary<string, Func<ZabbixItemKey, CancellationToken, ValueTask<string>>> _answers = new(StringComparer.Ordinal);
    private FrozenDictionary<string, Func<ZabbixItemKey, CancellationToken, ValueTask<string>>>? _frozen;

    public IReadOnlyList<ZabbixItemDefinition> Items => _items;

    /// <summary>A key answered at once.</summary>
    public ZabbixItemSet Add(string name, string description, Func<ZabbixItemKey, string> answer, string? testKey = null)
        => Add(name, description, (key, _) => ValueTask.FromResult(answer(key)), testKey);

    /// <summary>A key answered within the check's deadline.</summary>
    public ZabbixItemSet Add(string name, string description, Func<ZabbixItemKey, CancellationToken, ValueTask<string>> answer, string? testKey = null)
    {
        _items.Add(new ZabbixItemDefinition(name, description) { TestKey = testKey });
        _answers.Add(name, answer);
        return this;
    }

    public async ValueTask<ZabbixAgentAnswer> AnswerAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        _frozen ??= _answers.ToFrozenDictionary(StringComparer.Ordinal);
        if (!_frozen.TryGetValue(key.Name, out Func<ZabbixItemKey, CancellationToken, ValueTask<string>>? answer))
            return ZabbixAgentAnswer.NotSupported(key, "This provider does not answer the key.");
        try
        {
            return ZabbixAgentAnswer.Supported(key, await answer(key, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception refused) when (refused is ZabbixItemException or IOException or UnauthorizedAccessException or PlatformNotSupportedException
                                            or System.Net.Sockets.SocketException or System.Net.NetworkInformation.NetworkInformationException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return ZabbixAgentAnswer.NotSupported(key, refused.Message);
        }
    }

    /// <summary>The parameter at <paramref name="index"/>, or the empty string when the key has fewer.</summary>
    public static string Parameter(ZabbixItemKey key, int index) => index < key.Parameters.Count ? key.Parameters[index] : string.Empty;

    /// <summary>Refuses a key with more than <paramref name="max"/> parameters: "The key has more parameters than it takes."</summary>
    public static void AtMost(ZabbixItemKey key, int max)
    {
        if (key.Parameters.Count > max) throw ZabbixItemException.TooManyParameters();
    }

    /// <summary>
    /// The parameter at <paramref name="index"/> as one of <paramref name="modes"/>; empty is the first, the default.
    /// Anything else is "The key's … parameter is not valid."
    /// </summary>
    public static string Mode(ZabbixItemKey key, int index, params ReadOnlySpan<string> modes)
    {
        string value = Parameter(key, index);
        if (value.Length == 0) return modes[0];
        foreach (string mode in modes)
        {
            if (string.Equals(mode, value, StringComparison.Ordinal)) return mode;
        }
        throw ZabbixItemException.InvalidParameter(index);
    }

    /// <summary>The parameter at <paramref name="index"/> as a regular expression, or <see langword="null"/> when it is empty.</summary>
    /// <exception cref="ZabbixItemException">.NET cannot read the expression.</exception>
    public static System.Text.RegularExpressions.Regex? Pattern(ZabbixItemKey key, int index)
        => Parameter(key, index) is { Length: > 0 } text ? Expression(text, index) : null;

    /// <summary><paramref name="text"/>, the parameter at <paramref name="index"/>, as a regular expression that gives up after a second.</summary>
    /// <exception cref="ZabbixItemException">.NET cannot read the expression.</exception>
    public static System.Text.RegularExpressions.Regex Expression(string text, int index)
    {
        try
        {
            return new System.Text.RegularExpressions.Regex(text, System.Text.RegularExpressions.RegexOptions.CultureInvariant, s_patternTimeout);
        }
        catch (ArgumentException invalid)
        {
            throw ZabbixItemException.InvalidPattern(index, invalid);
        }
    }

    /// <summary>The seconds an <c>avg1</c>, <c>avg5</c> or <c>avg15</c> parameter names; empty is <c>avg1</c>.</summary>
    public static int Window(ZabbixItemKey key, int index) => Mode(key, index, "avg1", "avg5", "avg15") switch
    {
        "avg1" => 60,
        "avg5" => 300,
        _ => 900,
    };

    /// <summary>A duration in Zabbix's time suffixes (<c>30</c>, <c>30s</c>, <c>5m</c>, <c>1h</c>, <c>2d</c>, <c>1w</c>), read as <see cref="ZabbixDuration"/> reads one, or <see langword="null"/> when it is not one.</summary>
    public static TimeSpan? Duration(string text) => ZabbixDuration.TryParse(text, out ZabbixDuration duration) ? duration.Length : null;

    /// <summary>An unsigned integer, as Zabbix's agent writes one.</summary>
    public static string Integer(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>An integer, as Zabbix's agent writes one.</summary>
    public static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A floating-point value, as Zabbix's agent writes one: six decimals (<c>ZBX_FS_DBL</c>, <c>%lf</c>).</summary>
    public static string Float(double value) => value.ToString("F6", CultureInfo.InvariantCulture);

    /// <summary>A percentage of <paramref name="total"/>; "The total is zero, so there is no percentage of it." when it is.</summary>
    public static string Percent(double part, double total)
        => total == 0 ? throw new ZabbixItemException("The total is zero, so there is no percentage of it.") : Float(part / total * 100);

    /// <summary>A floating-point JSON member as <c>zbx_json_addfloat</c> writes one: six decimals.</summary>
    public static void WriteFloat(Utf8JsonWriter json, string name, double value)
    {
        json.WritePropertyName(name);
        json.WriteRawValue(Float(value), skipInputValidation: true);
    }

    /// <summary>A JSON value written by <paramref name="write"/>, compact, as Zabbix's agent writes one.</summary>
    public static string Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            write(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
