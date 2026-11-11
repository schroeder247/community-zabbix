// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix.Tests;

/// <summary>
/// Replies through the real deserialisation path — the source-generated <see cref="ZabbixApiJson"/>
/// context, the generated records and the SDK's converters (<see cref="ZabbixEnumConverter{TEnum}"/>,
/// <see cref="ZabbixFlagConverter"/>, <see cref="ZabbixObjectOrEmptyArrayConverter{T}"/>, <see cref="ZabbixIds"/>). Every
/// JSON form a value can take, in every modelled field type, must read or fail as <see cref="JsonException"/> (the
/// transport's <c>invalid_response</c>); never a <see cref="FormatException"/>, <see cref="OverflowException"/> or
/// <see cref="InvalidOperationException"/> out of a converter.
/// </summary>
public sealed class AdversarialJsonReplyTests
{
    /// <summary>Every form a JSON value can take, and some it cannot (those must be a JsonException from the reader).</summary>
    private static readonly string[] Forms =
    [
        "\"1\"", "1", "1.0", "\"1.0\"", "1e3", "\"1e3\"", "-0", "\"-0\"", "-1", "\"-1\"", "0.5", "\"0.5\"",
        "NaN", "\"NaN\"", "\"Infinity\"", "\"-Infinity\"", "1e400", "\"1e400\"", "-1e400",
        "99999999999999999999", "\"99999999999999999999\"", "9223372036854775808", "-9223372036854775809", "2147483648", "\"2147483648\"",
        "79228162514264337593543950336", "1.7976931348623157e309",
        "\"\"", "\" 1 \"", "\"1 \"", "\"+1\"", "\"0x10\"", "0x10", "\"1,5\"", "\"1, 3\"", "\"Active\"", "\"active\"", "\"Disabled, NotSupported\"",
        "null", "true", "false", "\"true\"", "\"TRUE\"", "\"yes\"", "[]", "[1]", "[[]]", "{}", "{\"a\":1}",
        "\"x\"", "\"\\u0000\"", "\"\\ud83d\\ude00\"", "\"\\ud800\"", "\"\\udc00\"", "\"\\ud800\\u0041\"", "\"\\\"\"", "\"\\/\"",
        "\"\u00e9\"", "'1'", "undefined", "1 2", "",
    ];

    private sealed record Target(string Field, Func<string, object?> Read);

    private static Func<string, object?> RowsOf<T>(JsonTypeInfo<IReadOnlyList<T>> info) => json => JsonSerializer.Deserialize(json, info);

    // One field of each modelled shape: int, long, double, a 0/1 flag, an enum, a string, an object-or-[] relation, a
    // list of records, a free JsonElement.
    private static readonly Target[] Targets =
    [
        new("maintenance_from", RowsOf(ZabbixApiJson.Default.IReadOnlyListZabbixHost)),
        new("lastclock", RowsOf(ZabbixApiJson.Default.IReadOnlyListZabbixItem)),
        new("value_avg", RowsOf(ZabbixApiJson.Default.IReadOnlyListZabbixTrendRecord)),
        new("allow_traps", RowsOf(ZabbixApiJson.Default.IReadOnlyListZabbixItem)),
        new("status", RowsOf(ZabbixApiJson.Default.IReadOnlyListZabbixItem)),
        new("flags", RowsOf(ZabbixApiJson.Default.IReadOnlyListZabbixHost)),
        new("name", RowsOf(ZabbixApiJson.Default.IReadOnlyListZabbixItem)),
        new("discoveryRule", RowsOf(ZabbixApiJson.Default.IReadOnlyListZabbixHost)),
        new("groups", RowsOf(ZabbixApiJson.Default.IReadOnlyListZabbixHost)),
        new("assigned_proxyid", RowsOf(ZabbixApiJson.Default.IReadOnlyListZabbixHost)),
    ];

    [Fact]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The invariant under test is that nothing but JsonException escapes; any other type is recorded as the failure.")]
    public void Every_json_form_in_every_modelled_field_type_reads_or_is_a_json_exception()
    {
        var broken = new List<string>();

        foreach (Target target in Targets)
        {
            foreach (string form in Forms)
            {
                string json = "[{\"" + target.Field + "\":" + form + "}]";
                try { target.Read(json); }
                catch (JsonException) { }
                catch (Exception other) { broken.Add($"{target.Field} = {form}: {other.GetType().Name}: {other.Message}"); }
            }
        }

        Assert.True(broken.Count == 0, $"{broken.Count} forms broke the contract:\n" + string.Join('\n', broken));
    }

    [Theory]
    [InlineData("\"42\"", 42)]
    [InlineData("42", 42)]
    [InlineData("\"-7\"", -7)]
    [InlineData("-0", 0)]
    public void Zabbix_s_numbers_as_strings_read_as_the_number(string form, int expected)
    {
        IReadOnlyList<ZabbixHost>? hosts = JsonSerializer.Deserialize("[{\"maintenance_from\":" + form + "}]", ZabbixApiJson.Default.IReadOnlyListZabbixHost);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expected), Assert.Single(hosts!).MaintenanceFrom);
    }

    [Theory]
    [InlineData("\"0.0000\"", 0.0)]
    [InlineData("\"12.5\"", 12.5)]
    [InlineData("12.5", 12.5)]
    [InlineData("\"1e3\"", 1000.0)]
    public void A_float_value_reads_from_zabbix_s_decimal_text(string form, double expected)
    {
        IReadOnlyList<ZabbixTrendRecord>? trends = JsonSerializer.Deserialize("[{\"value_avg\":" + form + "}]", ZabbixApiJson.Default.IReadOnlyListZabbixTrendRecord);

        Assert.Equal(expected, Assert.Single(trends!).ValueAverage);
    }

    // The generated context reads numbers from strings (JsonNumberHandling.AllowReadingFromString), and a plain double
    // would then read the strings "NaN" and "Infinity" as NaN and ∞, and the number literal 1e400 (past double's range)
    // as ∞. A Zabbix float never is one: the server refuses NaN and ±∞ before they are stored (zbx_validate_value_dbl,
    // src/libs/zbxnum/num.c: isnan/isinf → FAIL), so such a reply is not the API's and is refused, not read without a word.
    [Theory]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("1e400")]
    public void A_float_that_is_not_finite_is_refused_rather_than_read_as_infinity_or_nan(string form)
    {
        Exception? failure = Record.Exception(() => JsonSerializer.Deserialize("[{\"value_avg\":" + form + "}]", ZabbixApiJson.Default.IReadOnlyListZabbixTrendRecord));

        Assert.IsAssignableFrom<JsonException>(failure);
    }

    [Fact]
    public void A_float_string_past_double_s_range_is_refused()
        => Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize("[{\"value_avg\":\"1e400\"}]", ZabbixApiJson.Default.IReadOnlyListZabbixTrendRecord));

    [Theory]
    [InlineData("99")]
    [InlineData("\"99\"")]
    [InlineData("-1")]
    public void An_enum_code_the_model_does_not_know_survives_as_itself(string form)
    {
        IReadOnlyList<ZabbixItem>? items = JsonSerializer.Deserialize("[{\"status\":" + form + "}]", ZabbixApiJson.Default.IReadOnlyListZabbixItem);

        Assert.Equal(int.Parse(form.Trim('"'), System.Globalization.CultureInfo.InvariantCulture), (int)Assert.Single(items!).Status!.Value);
    }

    // Enum.TryParse reads a comma list of names as the OR of its members: "Disabled, NotSupported" would read as
    // ZabbixItemStatus 1|3 = 3 (NotSupported), for an enum that is not [Flags]. The API never writes one, and a wrong value
    // read silently is worse than a refusal, so ZabbixEnumConverter accepts a name only when it is one defined member.
    // (A list of numbers, "1, 3", is refused too: see the test below.)
    [Fact]
    public void A_comma_list_of_names_is_not_an_enum_value()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("[{\"status\":\"Disabled, NotSupported\"}]", ZabbixApiJson.Default.IReadOnlyListZabbixItem));

    [Fact]
    public void A_comma_list_of_numbers_is_not_an_enum_value()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("[{\"status\":\"1, 3\"}]", ZabbixApiJson.Default.IReadOnlyListZabbixItem));

    [Theory]
    [InlineData("\"0\"", false)]
    [InlineData("\"1\"", true)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void A_flag_reads_zabbix_s_zero_and_one_in_both_forms(string form, bool expected)
    {
        IReadOnlyList<ZabbixItem>? items = JsonSerializer.Deserialize("[{\"allow_traps\":" + form + "}]", ZabbixApiJson.Default.IReadOnlyListZabbixItem);

        Assert.Equal(expected, Assert.Single(items!).AllowTraps);
    }

    [Theory]
    [InlineData("[]", false)]
    [InlineData("null", false)]
    [InlineData("{\"itemid\":\"1\"}", true)]
    public void An_object_or_empty_array_relation_reads_the_empty_array_as_absent(string form, bool present)
    {
        IReadOnlyList<ZabbixHost>? hosts = JsonSerializer.Deserialize("[{\"discoveryRule\":" + form + "}]", ZabbixApiJson.Default.IReadOnlyListZabbixHost);

        Assert.Equal(present, Assert.Single(hosts!).DiscoveryRule is not null);
    }

    [Fact]
    public void Every_string_escape_decodes_to_its_character()
    {
        IReadOnlyList<ZabbixItem>? items = JsonSerializer.Deserialize("[{\"name\":\"\\u0000\\ud83d\\ude00\\\"\\\\\\/\\b\\f\\n\\r\\t\\u00e9\"}]", ZabbixApiJson.Default.IReadOnlyListZabbixItem);

        Assert.Equal("\u0000\U0001F600\"\\/\b\f\n\r\t\u00e9", Assert.Single(items!).Name);
    }

    // RFC 8259 leaves duplicate names to the reader; System.Text.Json's default (AllowDuplicateProperties) keeps the
    // last, and the frontend's json_encode never writes one.
    [Fact]
    public void A_duplicate_member_reads_as_its_last_value()
    {
        IReadOnlyList<ZabbixItem>? items = JsonSerializer.Deserialize("[{\"name\":\"first\",\"name\":\"last\"}]", ZabbixApiJson.Default.IReadOnlyListZabbixItem);

        Assert.Equal("last", Assert.Single(items!).Name);
    }

    [Fact]
    public void A_member_the_model_does_not_know_is_kept_in_the_additional_fields()
    {
        IReadOnlyList<ZabbixHost>? hosts = JsonSerializer.Deserialize("[{\"hostid\":\"1\",\"from_the_future\":{\"x\":[1,2]}}]", ZabbixApiJson.Default.IReadOnlyListZabbixHost);

        ZabbixHost host = Assert.Single(hosts!);
        Assert.Equal("{\"x\":[1,2]}", ((IZabbixAdditionalFields)host).AdditionalFields!["from_the_future"].GetRawText());
    }

    [Theory]
    [InlineData("[{\"name\":\"n\"")]
    [InlineData("[{\"name\":")]
    [InlineData("[{")]
    [InlineData("[")]
    public void A_truncated_reply_is_a_json_exception(string json)
        => Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(json, ZabbixApiJson.Default.IReadOnlyListZabbixItem));

    [Fact]
    public void Bytes_that_are_not_utf8_in_a_string_are_a_json_exception()
        => Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize([.. "[{\"name\":\""u8, 0xC3, 0x28, 0xFF, .. "\"}]"u8], ZabbixApiJson.Default.IReadOnlyListZabbixItem));

    // ---- ZabbixIds: the ids a create, update or delete returns ------------------------------------------------------

    [Theory]
    [InlineData("{\"hostids\":[\"10084\",\"10085\"]}", "10084,10085")]
    [InlineData("{\"hostids\":[10084]}", "10084")]
    [InlineData("{\"hostids\":[]}", "")]
    public void Ids_read_from_the_result_as_strings(string json, string expected)
    {
        using JsonDocument result = JsonDocument.Parse(json);

        Assert.Equal(expected, string.Join(',', ZabbixIds.Read(result.RootElement, "hostids")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"hostids\":\"10084\"}")]
    [InlineData("{\"hostids\":null}")]
    [InlineData("{\"hostids\":{}}")]
    public void A_result_without_its_id_array_is_invalid_data(string json)
    {
        using JsonDocument result = JsonDocument.Parse(json);

        Assert.Throws<InvalidDataException>(() => ZabbixIds.Read(result.RootElement, "hostids"));
    }

    // ZabbixIds.Read (Zabbix.Abstractions/Api/ZabbixJson.cs) refuses an id array element that is not a string or an
    // integer with its own contract for a malformed result, InvalidDataException (as for a missing array). The platform's
    // FormatException (1.5 and 1e400 through GetInt64) or InvalidOperationException (true, {} and [] through GetString)
    // must not escape, and a null element must not read as "", an id that names nothing, handed to the caller as a
    // created id.
    [Theory]
    [InlineData("{\"hostids\":[1.5]}")]
    [InlineData("{\"hostids\":[1e400]}")]
    [InlineData("{\"hostids\":[true]}")]
    [InlineData("{\"hostids\":[{}]}")]
    [InlineData("{\"hostids\":[[]]}")]
    [InlineData("{\"hostids\":[null]}")]
    public void An_id_that_is_neither_a_string_nor_an_integer_is_invalid_data(string json)
    {
        using JsonDocument result = JsonDocument.Parse(json);

        Assert.Throws<InvalidDataException>(() => ZabbixIds.Read(result.RootElement, "hostids"));
    }
}
