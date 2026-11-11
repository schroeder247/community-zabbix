// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Community.Zabbix.Items;

/// <summary>
/// The DNS client behind <c>net.dns</c>, <c>net.dns.perf</c> and <c>net.dns.record</c> (RFC 1035): it asks named servers
/// as the C library's <c>res_nsend</c> does, because the framework's resolver answers only addresses and asks only the
/// system's servers. Zabbix's agent reads the answer with <c>dns_query</c> (<c>common/dns.c</c>), whose text
/// <see cref="Records"/> writes.
/// </summary>
internal sealed class ZabbixDnsClient(TimeProvider time)
{
    public const ushort Ptr = 12;

    private const int HeaderBytes = 12;
    private const int MostUdpBytes = 65535;
    private const ushort RecursionDesired = 0x0100;
    private const ushort ClassInternet = 1;
    private const ushort ClassHesiod = 4;
    private const int ResponseCodeServerFailure = 2;
    private const int ResponseCodeNotImplemented = 4;
    private const int ResponseCodeRefused = 5;

    // The check's own deadline ends a wait long before a day, and a timer takes nothing past about 49 days.
    private const int MostWaitSeconds = 24 * 60 * 60;

    /// <summary>The record types Zabbix's keys name, and their numbers.</summary>
    public static ushort? TypeNumber(string name) => name.ToUpperInvariant() switch
    {
        "ANY" => 255,
        "A" => 1,
        "AAAA" => 28,
        "NS" => 2,
        "MD" => 3,
        "MF" => 4,
        "CNAME" => 5,
        "SOA" => 6,
        "MB" => 7,
        "MG" => 8,
        "MR" => 9,
        "NULL" => 10,
        "WKS" => 11,
        "PTR" => Ptr,
        "HINFO" => 13,
        "MINFO" => 14,
        "MX" => 15,
        "TXT" => 16,
        "SRV" => 33,
        _ => null,
    };

    /// <summary>The answer's response code.</summary>
    public static int ResponseCode(ReadOnlySpan<byte> message) => message[3] & 0x0F;

    /// <summary>How many records the answer section holds.</summary>
    public static int AnswerCount(ReadOnlySpan<byte> message) => BinaryPrimitives.ReadUInt16BigEndian(message[6..]);

    /// <summary>A question for one name and type, recursion asked for.</summary>
    /// <exception cref="ZabbixItemException">The name cannot be written as a DNS name.</exception>
    public static byte[] Query(string name, ushort type)
    {
        string[] labels = name.TrimEnd('.').Split('.');
        using var message = new MemoryStream();
        Span<byte> header = stackalloc byte[HeaderBytes];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1));
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], RecursionDesired);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        message.Write(header);
        foreach (string label in labels)
        {
            if (label.Length == 0 && labels.Length == 1) break;     // the root
            byte[] bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ZabbixItemException("The name cannot be asked about: a label is empty or longer than 63 bytes.");
            message.WriteByte((byte)bytes.Length);
            message.Write(bytes);
        }
        message.WriteByte(0);
        if (message.Length - HeaderBytes > 255) throw new ZabbixItemException("The name cannot be asked about: it is longer than 255 bytes.");
        Span<byte> question = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(question, type);
        BinaryPrimitives.WriteUInt16BigEndian(question[2..], ClassInternet);
        message.Write(question);
        return message.ToArray();
    }

    /// <summary>What <c>zbx_ip_reverse</c> makes of a PTR query's name: an address, reversed; a name already reversed, as it is.</summary>
    /// <exception cref="ZabbixItemException">The name is neither.</exception>
    public static string Reversed(string name)
    {
        if (name.Contains(".ip6.arpa", StringComparison.Ordinal) || name.Contains(".in-addr.arpa", StringComparison.Ordinal)) return name;
        if (IPAddress.TryParse(name, out IPAddress? address))
        {
            byte[] bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var nibbles = new StringBuilder();
                for (int i = bytes.Length - 1; i >= 0; i--)
                {
                    nibbles.Append(CultureInfo.InvariantCulture, $"{bytes[i] & 0x0F:x}.{bytes[i] >> 4:x}.");
                }
                return nibbles.Append("ip6.arpa").ToString();
            }
            if (name.Count(c => c == '.') == 3) return string.Create(CultureInfo.InvariantCulture, $"{bytes[3]}.{bytes[2]}.{bytes[1]}.{bytes[0]}.in-addr.arpa");
        }
        throw new ZabbixItemException($"\"{name}\" is neither an address nor a reverse name.");
    }

    /// <summary>
    /// Asks each server in turn, <paramref name="tries"/> times round, as <c>res_nsend</c> does: each try waits
    /// <paramref name="seconds"/> doubled by the try and shared among the servers, a truncated answer is asked again over
    /// TCP, and a server's failure, refusal or lack of the feature passes on to the next.
    /// </summary>
    /// <returns>The answer, or the last refusal; <see langword="null"/> when no server answered.</returns>
    public async ValueTask<byte[]?> ExchangeAsync(IReadOnlyList<IPEndPoint> servers, byte[] query, int seconds, int tries, bool tcp, CancellationToken cancellationToken)
    {
        byte[]? refusal = null;
        for (int attempt = 0; attempt < tries; attempt++)
        {
            foreach (IPEndPoint server in servers)
            {
                int wait = attempt >= 31 ? int.MaxValue : seconds << attempt;
                if (attempt > 0) wait /= servers.Count;
                if (wait <= 0) wait = 1;
                byte[]? answer = await AskAsync(server, query, TimeSpan.FromSeconds(Math.Min(wait, MostWaitSeconds)), tcp, cancellationToken).ConfigureAwait(false);
                if (answer is null) continue;
                if (ResponseCode(answer) is not (ResponseCodeServerFailure or ResponseCodeNotImplemented or ResponseCodeRefused)) return answer;
                refusal = answer;
            }
        }
        return refusal;
    }

    private async ValueTask<byte[]?> AskAsync(IPEndPoint server, byte[] query, TimeSpan wait, bool tcp, CancellationToken cancellationToken)
    {
        using var expiry = new CancellationTokenSource(wait, time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        try
        {
            if (tcp) return await AskTcpAsync(server, query, deadline.Token).ConfigureAwait(false);
            byte[] answer = await AskUdpAsync(server, query, deadline.Token).ConfigureAwait(false);
            const byte Truncated = 0x02;
            return (answer[2] & Truncated) == 0 ? answer : await AskTcpAsync(server, query, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception failed) when (failed is SocketException or IOException)
        {
            return null;
        }
    }

    private static async ValueTask<byte[]> AskUdpAsync(IPEndPoint server, byte[] query, CancellationToken cancellationToken)
    {
        // Connected, so the kernel passes up only what that server sends.
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await socket.ConnectAsync(server, cancellationToken).ConfigureAwait(false);
        await socket.SendAsync(query, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[MostUdpBytes];
        while (true)
        {
            int read = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (Answers(query, buffer.AsSpan(0, read))) return buffer[..read];
        }
    }

    private static async ValueTask<byte[]> AskTcpAsync(IPEndPoint server, byte[] query, CancellationToken cancellationToken)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(server, cancellationToken).ConfigureAwait(false);
        var stream = new NetworkStream(socket);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable closing = stream.ConfigureAwait(false);
        byte[] framed = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
        query.CopyTo(framed, 2);
        await stream.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
        byte[] length = new byte[2];
        await stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        byte[] answer = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
        await stream.ReadExactlyAsync(answer, cancellationToken).ConfigureAwait(false);
        return Answers(query, answer) ? answer : throw new IOException("The server's answer is to another question.");
    }

    // res_queryok and res_queriesmatch: this question's id, an answer, and this question, or a server that would not read it.
    private static bool Answers(ReadOnlySpan<byte> query, ReadOnlySpan<byte> answer)
    {
        const byte IsAnswer = 0x80;
        if (answer.Length < HeaderBytes || answer[0] != query[0] || answer[1] != query[1] || (answer[2] & IsAnswer) == 0) return false;
        int questions = BinaryPrimitives.ReadUInt16BigEndian(answer[4..]);
        if (questions == 0) return ResponseCode(answer) is 1 or ResponseCodeServerFailure or ResponseCodeNotImplemented or ResponseCodeRefused;
        int length = query.Length - HeaderBytes;
        return questions == 1 && answer.Length >= HeaderBytes + length && SameQuestion(query[HeaderBytes..], answer.Slice(HeaderBytes, length));
    }

    // Names compare without regard to case (ns_samename), and only the letters have one: a label or the type's bytes may
    // be anything (ANY is 0xFF), which is why this is not Ascii.EqualsIgnoreCase.
    private static bool SameQuestion(ReadOnlySpan<byte> asked, ReadOnlySpan<byte> answered)
    {
        static byte Folded(byte value) => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value | 0x20) : value;
        for (int i = 0; i < asked.Length; i++)
        {
            if (Folded(asked[i]) != Folded(answered[i])) return false;
        }
        return true;
    }

    /// <summary>The answer section as <c>net.dns.record</c> reports it: a line per record, sorted.</summary>
    /// <exception cref="ZabbixItemException">The answer cannot be read.</exception>
    public static string Records(byte[] message)
    {
        int offset = HeaderBytes;
        for (int questions = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4)); questions > 0 && offset < message.Length; questions--)
        {
            _ = Name(message, ref offset, "domain name");
            offset += 4;
        }
        var lines = new List<string>();
        for (int answers = AnswerCount(message); answers > 0 && offset < message.Length; answers--)
        {
            lines.Add(Record(message, ref offset));
        }
        lines.Sort(StringComparer.Ordinal);
        return string.Concat(lines).TrimEnd('\n');
    }

    private static string Record(byte[] message, ref int offset)
    {
        string name = Name(message, ref offset, "domain name");
        int type = UInt16(message, ref offset);
        int recordClass = UInt16(message, ref offset);
        offset += 4;            // the time to live
        int length = UInt16(message, ref offset);
        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"{name,-20} {TypeName(type),-8}");
        if (offset + length > message.Length) throw new ZabbixItemException("The DNS answer cannot be read: a record runs past its end.");
        int end = offset + length;
        switch (type)
        {
            case 1 when recordClass is ClassInternet or ClassHesiod && length == 4:
            case 28 when recordClass is ClassInternet or ClassHesiod && length == 16:
                line.Append(' ').Append(new IPAddress(message.AsSpan(offset, length)));
                break;
            case 2:
                AppendName(line, message, ref offset, "name server name");
                break;
            case 3:
                AppendName(line, message, ref offset, "mail destination name");
                break;
            case 4:
                AppendName(line, message, ref offset, "mail forwarder name");
                break;
            case 5:
                AppendName(line, message, ref offset, "canonical name");
                break;
            case 7:
                AppendName(line, message, ref offset, "mailbox name");
                break;
            case 8:
                AppendName(line, message, ref offset, "mail group name");
                break;
            case 9:
                AppendName(line, message, ref offset, "renamed mailbox name");
                break;
            case Ptr:
                AppendName(line, message, ref offset, "PTR name");
                break;
            case 15:
                line.Append(CultureInfo.InvariantCulture, $" {UInt16(message, ref offset)}");
                AppendName(line, message, ref offset, "mail exchange name");
                break;
            case 6:
                AppendName(line, message, ref offset, "source nameserver name");
                AppendName(line, message, ref offset, "administrator mailbox name");
                line.Append(CultureInfo.InvariantCulture, $" {UInt32(message, ref offset)}");
                for (int i = 0; i < 4; i++)
                {
                    line.Append(CultureInfo.InvariantCulture, $" {(int)UInt32(message, ref offset)}");
                }
                break;
            case 10:
                line.Append(CultureInfo.InvariantCulture, $" len:{length}");
                break;
            case 11:
                if (length < 5) throw new ZabbixItemException("The DNS answer cannot be read: a WKS record is too short.");
                WellKnownServices(message, offset, end, line);
                break;
            case 13:
                int stringEnd = offset;
                for (int i = 0; i < 2 && stringEnd < end; i++)
                {
                    int size = message[stringEnd];
                    if (stringEnd + 1 + size > end) throw new ZabbixItemException("The DNS answer cannot be read: a record runs past its end.");
                    if (size != 0) line.Append(" \"").Append(Encoding.UTF8.GetString(message, stringEnd + 1, size)).Append('"');
                    stringEnd += 1 + size;
                }
                break;
            case 14:
                AppendName(line, message, ref offset, "mailbox responsible for mailing lists");
                AppendName(line, message, ref offset, "mailbox for error messages");
                break;
            case 16:
                line.Append(" \"");
                using (var text = new MemoryStream())
                {
                    for (int at = offset; at < end;)
                    {
                        int size = Math.Min(message[at], end - at - 1);
                        text.Write(message, at + 1, size);
                        at += 1 + size;
                    }
                    line.Append(Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length));
                }
                line.Append('"');
                break;
            case 33:
                int priority = UInt16(message, ref offset), weight = UInt16(message, ref offset), port = UInt16(message, ref offset);
                line.Append(CultureInfo.InvariantCulture, $" {priority} {weight} {port}");
                AppendName(line, message, ref offset, "service target hostname");
                break;
        }
        offset = end;
        return line.Append('\n').ToString();
    }

    private static void AppendName(StringBuilder line, byte[] message, ref int offset, string what) => line.Append(' ').Append(Name(message, ref offset, what));

    // WKS: the framework has no services database, so the ports are the numbers Zabbix's agent falls back to.
    private static void WellKnownServices(byte[] message, int offset, int end, StringBuilder line)
    {
        line.Append(' ').Append(new IPAddress(message.AsSpan(offset, 4)));
        int protocol = message[offset + 4];
        line.Append(' ').Append(protocol switch
        {
            1 => "icmp",
            6 => "tcp",
            17 => "udp",
            _ => protocol.ToString(CultureInfo.InvariantCulture),
        });
        int port = 0;
        for (int at = offset + 5; at < end; at++)
        {
            for (int bit = 0x80; bit != 0; bit >>= 1, port++)
            {
                if ((message[at] & bit) != 0) line.Append(CultureInfo.InvariantCulture, $" #{port}");
            }
        }
    }

    private static string TypeName(int type) => type switch
    {
        1 => "A",
        28 => "AAAA",
        2 => "NS",
        3 => "MD",
        4 => "MF",
        5 => "CNAME",
        6 => "SOA",
        7 => "MB",
        8 => "MG",
        9 => "MR",
        10 => "NULL",
        11 => "WKS",
        Ptr => "PTR",
        13 => "HINFO",
        14 => "MINFO",
        15 => "MX",
        16 => "TXT",
        33 => "SRV",
        _ => string.Create(CultureInfo.InvariantCulture, $"T_{type}"),
    };

    // dn_expand: compressed names followed, the root as ".", and what would not read as text escaped as ns_name_ntop does.
    private static string Name(byte[] message, ref int offset, string what)
    {
        var name = new StringBuilder();
        int at = offset, hops = 0;
        bool jumped = false;
        while (true)
        {
            if (at >= message.Length) throw Unreadable(what);
            int size = message[at];
            if (size == 0)
            {
                if (!jumped) offset = at + 1;
                return name.Length == 0 ? "." : name.ToString();
            }
            if ((size & 0xC0) == 0xC0)
            {
                if (at + 1 >= message.Length || ++hops > 64) throw Unreadable(what);
                int target = ((size & 0x3F) << 8) | message[at + 1];
                if (!jumped) offset = at + 2;
                jumped = true;
                at = target;
                continue;
            }
            if ((size & 0xC0) != 0 || at + 1 + size > message.Length) throw Unreadable(what);
            if (name.Length != 0) name.Append('.');
            foreach (byte value in message.AsSpan(at + 1, size))
            {
                if (value is (byte)'"' or (byte)'.' or (byte)';' or (byte)'\\' or (byte)'(' or (byte)')' or (byte)'@' or (byte)'$') name.Append('\\').Append((char)value);
                else if (value is > 0x20 and < 0x7F) name.Append((char)value);
                else name.Append(CultureInfo.InvariantCulture, $"\\{value:000}");
            }
            at += 1 + size;
        }
    }

    private static ZabbixItemException Unreadable(string what) => new($"The DNS answer cannot be read: its {what} does not expand.");

    private static int UInt16(byte[] message, ref int offset)
    {
        if (offset + 2 > message.Length) throw new ZabbixItemException("The DNS answer cannot be read: a record runs past its end.");
        int value = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset));
        offset += 2;
        return value;
    }

    private static uint UInt32(byte[] message, ref int offset)
    {
        if (offset + 4 > message.Length) throw new ZabbixItemException("The DNS answer cannot be read: a record runs past its end.");
        uint value = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(offset));
        offset += 4;
        return value;
    }
}
