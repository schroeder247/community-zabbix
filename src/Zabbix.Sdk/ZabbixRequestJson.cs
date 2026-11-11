// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix;

/// <summary>
/// Writes the JSON-RPC request as it goes on the wire: the envelope written straight to UTF-8 with the caller's type info,
/// and never a string the writer changed on the way (<see cref="ZabbixText"/>: a lone surrogate is refused as
/// <see cref="ArgumentException"/>, not sent as U+FFFD).
/// </summary>
/// <remarks>
/// The check costs a request nothing it did not already pay unless the written bytes hold U+FFFD (as <c>\uFFFD</c>, the
/// default encoder's form, or raw): one vectorized search of the envelope. Only then are the parameters written again,
/// to nowhere, through the same type info with a string converter that refuses a lone surrogate, which tells a caller's
/// own U+FFFD (sent as it is) from one the writer put in place of a surrogate (refused). A custom
/// <see cref="System.Text.Encodings.Web.JavaScriptEncoder"/> would need <c>unsafe</c> pointer overrides, and the strict
/// converter on every request costs about a quarter more time per call for a case that almost never happens.
/// </remarks>
internal static class ZabbixRequestJson
{
    private static readonly ConditionalWeakTable<JsonTypeInfo, JsonTypeInfo> s_strict = new();

    /// <summary>The request <c>{"jsonrpc":"2.0","method":…,"params":…,"id":…}</c>.</summary>
    /// <exception cref="ArgumentException">A string in <paramref name="method"/> or <paramref name="parameters"/> holds a lone surrogate.</exception>
    public static ReadOnlyMemory<byte> Envelope<TParameters>(string method, TParameters parameters, JsonTypeInfo<TParameters> parametersInfo, long id)
    {
        ZabbixText.ThrowIfLoneSurrogate(method, nameof(method));
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc"u8, "2.0"u8);
            writer.WriteString("method"u8, method);
            writer.WritePropertyName("params"u8);
            JsonSerializer.Serialize(writer, parameters, parametersInfo);
            writer.WriteNumber("id"u8, id);
            writer.WriteEndObject();
        }
        ReadOnlySpan<byte> written = buffer.WrittenSpan;
        if (written.IndexOf("\\uFFFD"u8) >= 0 || written.IndexOf("\uFFFD"u8) >= 0)
        {
            using var nowhere = new Utf8JsonWriter(Stream.Null);
            JsonSerializer.Serialize(nowhere, parameters, s_strict.GetValue(parametersInfo, static info => Refusing(info)));
        }
        return buffer.WrittenMemory;
    }

    private static JsonTypeInfo Refusing(JsonTypeInfo info)
    {
        var options = new JsonSerializerOptions(info.Options);
        options.Converters.Insert(0, new RefusingLoneSurrogates());
        options.MakeReadOnly();
        return options.GetTypeInfo(info.Type);
    }

    /// <summary>Strings as the platform writes them, refusing a lone surrogate first; owned because the platform writes one as U+FFFD instead of refusing it.</summary>
    private sealed class RefusingLoneSurrogates : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            ZabbixText.ThrowIfLoneSurrogate(value, "parameters");
            writer.WriteStringValue(value);
        }

        public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() ?? string.Empty;

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            ZabbixText.ThrowIfLoneSurrogate(value, "parameters");
            writer.WritePropertyName(value);
        }
    }
}
