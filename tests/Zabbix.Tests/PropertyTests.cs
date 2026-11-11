// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Protocol;
using CsCheck;

namespace Community.Zabbix.Tests;

/// <summary>
/// The parsers and formatters as properties over generated inputs (CsCheck): each holds for every input the generator
/// draws, and a failure is shrunk to the smallest input that breaks it and printed with the seed that replays it. The
/// example and corpus tests beside each type pin the cases upstream names; these pin the laws between them.
/// </summary>
public sealed class PropertyTests
{
    private const string NameChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-";

    // The characters a key parameter's quoting turns on, and enough ordinary ones to fill the gaps between them.
    private const string ParameterChars = "ab1 \",[]\\.:/-*";

    private static readonly Gen<string> KeyName = Gen.String[Gen.Char[NameChars], 1, 12];

    private static readonly Gen<string[]> KeyParameters = Gen.String[Gen.Char[ParameterChars], 0, 8].Array[0, 5];

    [Fact]
    public void An_item_key_made_from_its_parts_reads_back_as_the_same_key_and_the_same_parts()
    {
        Gen.Select(KeyName, KeyParameters).Sample((name, parameters) =>
        {
            bool needsQuotes(string p) => p.Length > 0 && (p[0] is '"' or ' ' or '[' || p.AsSpan().IndexOfAny(",]") >= 0);
            bool made = ZabbixItemKey.TryCreate(name, parameters, out ZabbixItemKey key);

            // the one refusal the quoting rule names: a parameter that needs quotes and ends with a backslash
            Assert.Equal(!parameters.Any(p => needsQuotes(p) && p.EndsWith('\\')), made);
            if (!made) return;
            ZabbixItemKey back = ZabbixItemKey.Parse(key.ToString());
            Assert.Equal(key, back);
            Assert.Equal(name, back.Name);
            Assert.Equal(parameters, back.Parameters);
        }, iter: 5_000);
    }

    [Fact]
    public void Reading_any_text_as_an_item_key_answers_and_never_throws()
    {
        Gen.String[Gen.Char[NameChars + ParameterChars + "\n\té"], 0, 40].Sample(text =>
        {
            if (ZabbixItemKey.TryParse(text, out ZabbixItemKey key)) Assert.Equal(text, key.ToString());
        }, iter: 10_000);
    }

    [Fact]
    public void A_frame_reads_back_as_its_payload_in_both_modes_and_every_proper_prefix_asks_for_more()
    {
        Gen.Select(Gen.Byte.Array[0, 600], Gen.Bool).Sample((payload, compress) =>
        {
            byte[] frame = Frame(payload, compress);

            var whole = new ReadOnlySequence<byte>(frame);
            Assert.True(ZabbixHeader.TryRead(ref whole, out ReadOnlyMemory<byte> read));
            Assert.Equal(payload, read.ToArray());
            Assert.True(whole.IsEmpty);
            for (int length = 0; length < frame.Length; length++)
            {
                var prefix = new ReadOnlySequence<byte>(frame, 0, length);
                Assert.False(ZabbixHeader.TryRead(ref prefix, out _));
                Assert.Equal(length, prefix.Length);                                       // nothing consumed until the frame is whole
            }
        }, iter: 300);
    }

    [Fact]
    public void A_frame_split_across_segments_reads_as_the_same_payload()
    {
        Gen.Select(Gen.Byte.Array[0, 400], Gen.Bool, Gen.Int[1, 40]).Sample((payload, compress, segment) =>
        {
            byte[] frame = Frame(payload, compress);
            ReadOnlySequence<byte> chunked = Chunked(frame, segment);

            Assert.True(ZabbixHeader.TryRead(ref chunked, out ReadOnlyMemory<byte> read));
            Assert.Equal(payload, read.ToArray());
        }, iter: 1_000);
    }

    /// <summary>
    /// The frame reader as a fuzz target: valid frames with bytes flipped, cut and appended, and bytes from nowhere. The
    /// reader answers a payload, asks for more, or refuses with <see cref="InvalidDataException"/>; no other exception,
    /// and a declared length above the cap is refused before the body is waited for.
    /// </summary>
    [Fact]
    public void The_frame_reader_answers_a_payload_more_or_invalid_data_for_any_bytes()
    {
        Gen<byte[]> mutated = Gen.Select(Gen.Byte.Array[0, 200], Gen.Bool, Gen.Int[0, 300].Array[1, 4], Gen.Byte.Array[1, 4], Gen.Int[-2, 20])
            .Select((payload, compress, positions, values, cut) =>
            {
                byte[] frame = Frame(payload, compress);
                for (int i = 0; i < positions.Length; i++) frame[positions[i] % frame.Length] = values[i % values.Length];
                return cut >= 0 ? frame[..Math.Max(0, frame.Length - cut)] : [.. frame, .. values];
            });
        Gen.OneOf(mutated, Gen.Byte.Array[0, 64]).Sample(bytes =>
        {
            var buffer = new ReadOnlySequence<byte>(bytes);
            try
            {
                if (ZabbixHeader.TryRead(ref buffer, out ReadOnlyMemory<byte> payload, maxPayload: 1024)) Assert.True(payload.Length <= 1024);
                else Assert.Equal(bytes.Length, buffer.Length);
            }
            catch (InvalidDataException)
            {
                // refused: the reader's one way to say the bytes are not a frame it takes
            }
        }, iter: 20_000);
    }

    [Fact]
    public void A_server_active_list_formats_and_reads_back_as_the_same_list()
    {
        Gen<ZabbixServerNode> node = Gen.Select(Host, Gen.Int[1, 65535]).Select((host, port) => new ZabbixServerNode(host, port));
        Gen<ZabbixServerNode[][]> destinations = node.Array[1, 3].Array[1, 4]
            .Where(list => list.SelectMany(d => d).Distinct().Count() == list.Sum(d => d.Length));    // a node appears once

        destinations.Sample(list =>
        {
            ZabbixServerActive active = list.Aggregate(ZabbixServerActive.Empty, (all, nodes) => all.WithDestination(nodes));
            string text = active.ToString();

            ZabbixServerActive back = ZabbixServerActive.Parse(text, CultureInfo.InvariantCulture);
            Assert.Equal(active, back);
            Assert.Equal(text, back.ToString());
            Span<char> chars = stackalloc char[text.Length];
            Assert.True(active.TryFormat(chars, out int written, default, CultureInfo.InvariantCulture));
            Assert.Equal(text, chars[..written].ToString());
            Span<byte> utf8 = stackalloc byte[128];
            foreach (ZabbixServerNode one in active.Nodes)
            {
                Assert.True(one.TryFormat(utf8, out int bytes, default, CultureInfo.InvariantCulture));
                Assert.Equal(one.ToString(), Encoding.UTF8.GetString(utf8[..bytes]));
            }
        }, iter: 2_000);
    }

    [Fact]
    public void A_plain_update_interval_is_due_strictly_after_now_and_within_one_interval()
    {
        Gen.Select(Gen.Int[1, 86_400], Gen.Long[0, 4_000_000_000_000], Gen.Long).Sample((seconds, ms, seed) =>
        {
            ZabbixItemDelay delay = ZabbixItemDelay.Parse(seconds.ToString(CultureInfo.InvariantCulture) + "s", CultureInfo.InvariantCulture);
            DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(ms);

            DateTimeOffset next = delay.GetNextCheck(now, TimeZoneInfo.Utc, seed)!.Value;

            Assert.Equal(TimeSpan.FromSeconds(seconds), delay.UpdateInterval);
            Assert.True(next > now, $"{next:O} is not after {now:O}");
            Assert.True(next <= now + delay.UpdateInterval, $"{next:O} is more than {seconds}s after {now:O}");
            Assert.Equal(delay, ZabbixItemDelay.Parse(delay.ToString(), CultureInfo.InvariantCulture));
        }, iter: 5_000);
    }

    [Fact]
    public void A_flexible_update_interval_is_due_after_now_and_moves_by_whole_weeks_with_the_clock()
    {
        // delays that divide a week, so a phase seeded by the item id repeats from one week to the next
        Gen<int> divisor = Gen.OneOfConst(1, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 21600, 43200, 86400);
        Gen<string> flexible = Gen.Select(divisor, Gen.Int[1, 7], Gen.Int[0, 6], Gen.Int[0, 23], Gen.Int[1, 24])
            .Select((every, from, span, start, hours) => string.Create(CultureInfo.InvariantCulture,
                $"{every}/{from}-{Math.Min(7, from + span)},{start:00}:00-{Math.Min(24, start + hours):00}:00"));
        Gen.Select(divisor, flexible.Array[0, 3], Gen.Long[0, 4_000_000_000_000], Gen.Long, Gen.Int[1, 3]).Sample((every, windows, ms, seed, weeks) =>
        {
            string text = string.Join(';', [every.ToString(CultureInfo.InvariantCulture) + "s", .. windows]);
            if (!ZabbixItemDelay.TryParse(text, CultureInfo.InvariantCulture, out ZabbixItemDelay? delay)) return;   // an empty window: upstream refuses it too
            DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            TimeSpan shift = TimeSpan.FromDays(7 * weeks);

            DateTimeOffset? next = delay.GetNextCheck(now, TimeZoneInfo.Utc, seed);
            DateTimeOffset? later = delay.GetNextCheck(now + shift, TimeZoneInfo.Utc, seed);

            Assert.NotNull(next);
            Assert.True(next > now, $"{next:O} is not after {now:O} for {text}");
            Assert.Equal(next + shift, later);
        }, iter: 3_000);
    }

    [Fact]
    public void A_clock_keeps_a_date_to_the_tick_and_orders_as_its_seconds_then_nanoseconds()
    {
        Gen<DateTimeOffset> instant = Gen.Long[DateTimeOffset.MinValue.UtcTicks, DateTimeOffset.MaxValue.UtcTicks].Select(ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
        Gen<ZabbixClock> clock = Gen.Select(Gen.Long[-62_135_596_800, 253_402_300_799], Gen.Int[0, 999_999_999]).Select((s, ns) => new ZabbixClock(s, ns));

        instant.Sample(time =>
        {
            ZabbixClock converted = ZabbixClock.FromDateTimeOffset(time);
            Assert.Equal(time, converted.ToDateTimeOffset());
            Assert.InRange(converted.Nanoseconds, 0, 999_999_999);
        }, iter: 5_000);
        Gen.Select(clock, clock).Sample((a, b) =>
        {
            Assert.Equal((a.Seconds, a.Nanoseconds).CompareTo((b.Seconds, b.Nanoseconds)), a.CompareTo(b));
            Assert.Equal(a, ZabbixClock.Parse(a.ToString(), CultureInfo.InvariantCulture));
            ZabbixClock ticked = ZabbixClock.FromDateTimeOffset(a.ToDateTimeOffset());          // a tick is 100 ns: rounded down, never up
            Assert.Equal(a.Seconds, ticked.Seconds);
            Assert.InRange(a.Nanoseconds - ticked.Nanoseconds, 0, 99);
        }, iter: 5_000);
    }

    [Fact]
    public void A_record_written_with_the_public_options_reads_back_equal_whatever_its_text_holds()
    {
        Gen<string> text = Gen.String[Gen.OneOf(Gen.Char, Gen.Char["\"\\/\u0000\u001f\u2028<>&'"]), 0, 20];
        Gen.Select(Gen.ULong[1, ulong.MaxValue], text, text, Gen.Enum<ZabbixHostStatus>(), Gen.Long[0, 4_102_444_800], Gen.Int[0, 3])
            .Sample((id, name, description, status, from, accept) =>
            {
                var host = new ZabbixHost
                {
                    HostId = id,
                    TechnicalName = name,
                    Description = description,
                    Status = status,
                    MaintenanceFrom = DateTimeOffset.FromUnixTimeSeconds(from),
                    TlsAccept = (ZabbixTlsAcceptModes)(1 << accept) & (ZabbixTlsAcceptModes)7,
                };

                string json = JsonSerializer.Serialize(host, ZabbixJson.Options);
                ZabbixHost? back = JsonSerializer.Deserialize<ZabbixHost>(json, ZabbixJson.Options);

                Assert.Equal(host, back);
                Assert.Equal(json, JsonSerializer.Serialize(back, ZabbixJson.Options));
            }, iter: 3_000);
    }

    [Fact]
    public void A_psk_reads_back_as_its_bytes_in_either_case_and_any_other_text_is_refused()
    {
        Gen.Select(Gen.Byte.Array[16, 256], Gen.Bool).Sample((key, lower) =>
        {
            string hex = Convert.ToHexString(key);
            Assert.Equal(key, ZabbixTls.ParsePsk(lower ? Convert.ToHexStringLower(key) : hex).ToArray());
        }, iter: 2_000);
        Gen.String[Gen.Char["0123456789abcdefABCDEFgx: "], 1, 600].Sample(text =>
        {
            bool valid = text.Length % 2 == 0 && text.Length is >= 32 and <= 512 && text.All(char.IsAsciiHexDigit);
            if (valid) Assert.Equal(text.Length / 2, ZabbixTls.ParsePsk(text).Length);
            else if (!string.IsNullOrWhiteSpace(text)) Assert.Throws<FormatException>(() => ZabbixTls.ParsePsk(text));
        }, iter: 5_000);
    }

    // A host name from DNS labels, an IPv4 address or an IPv6 address (written by IPAddress, so its canonical text).
    private static readonly Gen<string> Host = Gen.OneOf(
        Gen.Select(Gen.Char['a', 'z'], Gen.String[Gen.Char["abcdefghijklmnopqrstuvwxyz0123456789-"], 0, 6], Gen.Char["abcdefghijklmnopqrstuvwxyz0123456789"])
            .Select((first, middle, last) => first + middle + last).Array[1, 3].Select(labels => string.Join('.', labels)),
        Gen.Byte.Array[4].Select(bytes => new IPAddress(bytes).ToString()),
        Gen.Byte.Array[16].Select(bytes => new IPAddress(bytes).ToString()));

    private static byte[] Frame(byte[] payload, bool compress)
    {
        var writer = new ArrayBufferWriter<byte>();
        ZabbixHeader.Write(writer, payload, compress);
        return writer.WrittenSpan.ToArray();
    }

    private static ReadOnlySequence<byte> Chunked(byte[] bytes, int size)
    {
        if (bytes.Length <= size) return new ReadOnlySequence<byte>(bytes);
        var first = new Segment(bytes.AsMemory(0, size), 0);
        Segment last = first;
        for (int at = size; at < bytes.Length; at += size) last = last.Append(bytes.AsMemory(at, Math.Min(size, bytes.Length - at)));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
