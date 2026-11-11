// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Community.Zabbix.Items;

namespace Community.Zabbix.Tests;

/// <summary>
/// <c>net.dns</c>, <c>net.dns.perf</c> and <c>net.dns.record</c> (<c>common/dns.c</c>) against a DNS server of the test's
/// own on the loopback interface: the wire format, the text each record is reported as, and what a server's silence,
/// refusal or truncation comes to.
/// </summary>
public sealed class DnsItemsTests : IDisposable
{
    private const ushort A = 1, Ns = 2, Md = 3, Mf = 4, Cname = 5, Soa = 6, Mb = 7, Mg = 8, Mr = 9, Null = 10, Wks = 11, Ptr = 12, Hinfo = 13, Minfo = 14, Mx = 15, Txt = 16, Aaaa = 28, Srv = 33;

    private static readonly byte[] s_owner = [0xC0, 0x0C];          // a pointer to the question's name

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"zbx-dns-{Guid.NewGuid():N}");
    private readonly FakeServer _server = new();
    private readonly ZabbixNetworkItems _items;

    public DnsItemsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "etc"));
        File.WriteAllText(Path.Combine(_root, "etc", "resolv.conf"), "# the test's server\nsearch example\nnameserver 127.0.0.1\n");
        _items = new ZabbixNetworkItems(new ZabbixHostFiles(_root, linux: true), TimeProvider.System) { DnsPort = _server.Port };
    }

    public void Dispose()
    {
        _server.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_timeout_past_what_a_timer_takes_still_asks()
    {
        _server.Udp = query => Reply(query, 0, false, Record(A, [192, 0, 2, 1]));
        Assert.Equal("1", await ValueAsync("net.dns[,example.org,,5000000]"));
    }

    private async Task<string> ValueAsync(string key)
    {
        ZabbixAgentAnswer answer = await _items.GetValueAsync(ZabbixItemKey.Parse(key), Ct);
        Assert.Null(_server.Failure);
        Assert.True(answer.IsSupported, $"{key}: {answer.Error}");
        return answer.Value!;
    }

    private async Task<string?> ErrorAsync(string key)
    {
        ZabbixAgentAnswer answer = await _items.GetValueAsync(ZabbixItemKey.Parse(key), Ct);
        Assert.Null(_server.Failure);
        Assert.False(answer.IsSupported, $"{key} answered {answer.Value}");
        return answer.Error;
    }

    private static byte[] Name(string name)
    {
        using var bytes = new MemoryStream();
        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            bytes.WriteByte((byte)label.Length);
            bytes.Write(Encoding.Latin1.GetBytes(label));
        }
        bytes.WriteByte(0);
        return bytes.ToArray();
    }

    private static byte[] Join(params byte[][] parts) => [.. parts.SelectMany(part => part)];

    private static byte[] U16(int value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value);
        return bytes;
    }

    private static byte[] U32(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Record(ushort type, byte[] data, byte[]? owner = null, ushort recordClass = 1)
        => Join(owner ?? s_owner, U16(type), U16(recordClass), U32(300), U16(data.Length), data);

    private static byte[] Reply(byte[] query, int code, bool truncated, params byte[][] records)
    {
        byte[] response = Join(query, Join(records));
        response[2] = (byte)(0x81 | (truncated ? 0x02 : 0));        // an answer, recursion asked for
        response[3] = (byte)(0x80 | code);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6), (ushort)records.Length);
        return response;
    }

    private static string QuestionName(byte[] query)
    {
        var labels = new List<string>();
        for (int at = 12; query[at] != 0; at += query[at] + 1) labels.Add(Encoding.ASCII.GetString(query, at + 1, query[at]));
        return string.Join('.', labels);
    }

    private static int QuestionType(byte[] query)
    {
        int at = 12;
        while (query[at] != 0) at += query[at] + 1;
        return BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(at + 1));
    }

    [Fact]
    public async Task Every_record_type_is_reported_as_Zabbix_reports_it()
    {
        byte[] wellKnown = new byte[16];
        new byte[] { 192, 0, 2, 2, 6 }.CopyTo(wellKnown, 0);
        wellKnown[5 + 3] = 0x40;        // port 25
        wellKnown[5 + 10] = 0x80;       // port 80
        _server.Udp = query => Reply(query, 0, false,
            Record(A, [192, 0, 2, 1]),
            Record(Aaaa, IPAddress.Parse("2001:db8::1").GetAddressBytes()),
            Record(Ns, Name("ns1.example.org")),
            Record(Md, Name("md.example.org")),
            Record(Mf, Name("mf.example.org")),
            Record(Cname, Name("canon.example.org")),
            Record(Soa, Join(Name("ns.example.org"), Name("admin.example.org"), U32(4_000_000_000), U32(7200), U32(3600), U32(1209600), U32(300))),
            Record(Mb, Name("mb.example.org")),
            Record(Mg, Name("mg.example.org")),
            Record(Mr, Name("mr.example.org")),
            Record(Null, [1, 2, 3]),
            Record(Wks, wellKnown),
            Record(Ptr, Name("ptr.example.org")),
            Record(Hinfo, Join([3], Encoding.ASCII.GetBytes("cpu"), [2], Encoding.ASCII.GetBytes("os"))),
            Record(Minfo, Join(Name("rm.example.org"), Name("em.example.org"))),
            Record(Mx, Join(U16(10), Name("mail.example.org"))),
            Record(Txt, Join([4], Encoding.ASCII.GetBytes("one "), [3], Encoding.ASCII.GetBytes("two"))),
            Record(Srv, Join(U16(1), U16(2), U16(443), Name("srv.example.org"))),
            Record(99, [9, 9]));
        string[] expected =
        [
            "example.org          A        192.0.2.1",
            "example.org          AAAA     2001:db8::1",
            "example.org          NS       ns1.example.org",
            "example.org          MD       md.example.org",
            "example.org          MF       mf.example.org",
            "example.org          CNAME    canon.example.org",
            "example.org          SOA      ns.example.org admin.example.org 4000000000 7200 3600 1209600 300",
            "example.org          MB       mb.example.org",
            "example.org          MG       mg.example.org",
            "example.org          MR       mr.example.org",
            "example.org          NULL     len:3",
            "example.org          WKS      192.0.2.2 tcp #25 #80",
            "example.org          PTR      ptr.example.org",
            "example.org          HINFO    \"cpu\" \"os\"",
            "example.org          MINFO    rm.example.org em.example.org",
            "example.org          MX       10 mail.example.org",
            "example.org          TXT      \"one two\"",
            "example.org          SRV      1 2 443 srv.example.org",
            "example.org          T_99    ",
        ];
        Array.Sort(expected, StringComparer.Ordinal);       // zbx_vector_str_sort: the lines are sorted
        Assert.Equal(string.Join('\n', expected), await ValueAsync("net.dns.record[127.0.0.1,example.org,ANY]"));
        Assert.Equal(255, QuestionType(_server.LastQuestion!));
        Assert.Equal("example.org", QuestionName(_server.LastQuestion!));
    }

    [Fact]
    public async Task Names_are_decompressed_and_escaped()
    {
        byte[] label = Join([3], Encoding.Latin1.GetBytes("a.b"), [3], Encoding.Latin1.GetBytes("c d"), [1], [0xFF], [0]);
        _server.Udp = query => Reply(query, 0, false, Record(Ns, Join([4], Encoding.ASCII.GetBytes("host"), s_owner)), Record(Ns, [0]), Record(Cname, label));
        string[] lines = (await ValueAsync("net.dns.record[,example.org,ANY]")).Split('\n');
        Assert.Contains("example.org          NS       host.example.org", lines);
        Assert.Contains("example.org          NS       .", lines);
        Assert.Contains(@"example.org          CNAME    a\.b.c\032d.\255", lines);
    }

    [Fact]
    public async Task The_default_type_is_SOA_and_a_type_is_any_case()
    {
        _server.Udp = query => Reply(query, 0, false, Record(A, [192, 0, 2, 1]));
        Assert.Equal("1", await ValueAsync("net.dns[,example.org]"));
        Assert.Equal(Soa, QuestionType(_server.LastQuestion!));
        Assert.Equal("1", await ValueAsync("net.dns[,example.org,mx]"));
        Assert.Equal(Mx, QuestionType(_server.LastQuestion!));
    }

    [Fact]
    public async Task A_PTR_query_reverses_an_address()
    {
        _server.Udp = query => Reply(query, 0, false, Record(Ptr, Name("host.example.org")));
        Assert.Equal("7.2.0.192.in-addr.arpa PTR      host.example.org", await ValueAsync("net.dns.record[,192.0.2.7,PTR]"));
        Assert.Equal("7.2.0.192.in-addr.arpa", QuestionName(_server.LastQuestion!));
        _ = await ValueAsync("net.dns[,2001:db8::1,PTR]");
        Assert.Equal("1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.8.b.d.0.1.0.0.2.ip6.arpa", QuestionName(_server.LastQuestion!));
        _ = await ValueAsync("net.dns[,9.9.9.9.in-addr.arpa,PTR]");
        Assert.Equal("9.9.9.9.in-addr.arpa", QuestionName(_server.LastQuestion!));
        Assert.Equal("\"host.example.org\" is neither an address nor a reverse name.", await ErrorAsync("net.dns[,host.example.org,PTR]"));
    }

    [Fact]
    public async Task A_server_may_answer_that_it_has_nothing()
    {
        _server.Udp = query => Reply(query, 3, false);      // NXDOMAIN
        Assert.Equal("0", await ValueAsync("net.dns[,missing.example.org,A]"));
        Assert.True(double.Parse(await ValueAsync("net.dns.perf[,missing.example.org,A]"), CultureInfo.InvariantCulture) > 0);
        Assert.Equal("The DNS query has no answer to report.", await ErrorAsync("net.dns.record[,missing.example.org,A]"));
        _server.Udp = query => Reply(query, 0, false);      // an empty answer
        Assert.Equal("0", await ValueAsync("net.dns[,example.org,A]"));
        Assert.Equal("The DNS query has no answer to report.", await ErrorAsync("net.dns.record[,example.org,A]"));
    }

    [Fact]
    public async Task A_silent_server_is_down_with_no_time()
    {
        _server.Udp = _ => null;
        Assert.Equal("0", await ValueAsync("net.dns[127.0.0.1,example.org,A,1,1]"));
        Assert.Equal("0.000000", await ValueAsync("net.dns.perf[127.0.0.1,example.org,A,1,1]"));
        Assert.Equal("The DNS query has no answer to report.", await ErrorAsync("net.dns.record[127.0.0.1,example.org,A,1,1]"));
    }

    [Fact]
    public async Task A_silent_server_is_asked_as_many_times_as_the_key_says()
    {
        _server.Udp = _ => null;
        Assert.Equal("0", await ValueAsync("net.dns[,example.org,A,1,2]"));
        Assert.Equal(2, _server.UdpQuestions);
    }

    [Fact]
    public async Task An_answer_to_another_question_is_not_the_answer()
    {
        _server.Udp = query =>
        {
            byte[] wrong = Reply(query, 0, false, Record(A, [203, 0, 113, 1]));
            wrong[0] ^= 0xFF;
            _server.SendUdp(wrong);
            return Reply(query, 0, false, Record(A, [192, 0, 2, 1]));
        };
        Assert.Equal("example.org          A        192.0.2.1", await ValueAsync("net.dns.record[,example.org,A]"));
    }

    [Fact]
    public async Task An_answer_may_write_the_name_in_another_case()
    {
        _server.Udp = query =>
        {
            byte[] reply = Reply(query, 0, false, Record(A, [192, 0, 2, 1]));
            reply[13] = (byte)char.ToUpperInvariant((char)reply[13]);
            return reply;
        };
        Assert.Equal("1", await ValueAsync("net.dns[,example.org,A]"));
    }

    [Fact]
    public async Task A_truncated_answer_is_asked_again_over_TCP()
    {
        _server.Udp = query => Reply(query, 0, truncated: true);
        _server.Tcp = query => Reply(query, 0, false, Record(A, [192, 0, 2, 1]));
        Assert.Equal("example.org          A        192.0.2.1", await ValueAsync("net.dns.record[,example.org,A]"));
        Assert.Equal(1, _server.UdpQuestions);
        Assert.Equal(1, _server.TcpQuestions);
    }

    [Fact]
    public async Task The_key_can_ask_for_TCP()
    {
        _server.Tcp = query => Reply(query, 0, false, Record(A, [192, 0, 2, 1]));
        Assert.Equal("1", await ValueAsync("net.dns[,example.org,A,1,1,tcp]"));
        Assert.Equal(0, _server.UdpQuestions);
        Assert.Equal(1, _server.TcpQuestions);
    }

    [Fact]
    public async Task A_refusal_passes_to_the_next_server_and_has_a_time_when_all_refuse()
    {
        using var second = new FakeServer();
        _server.Udp = query => Reply(query, 5, false);      // REFUSED
        second.Udp = query => Reply(query, 0, false, Record(A, [192, 0, 2, 9]));
        var client = new ZabbixDnsClient(TimeProvider.System);
        byte[] query = ZabbixDnsClient.Query("example.org", A);
        IPEndPoint[] servers = [new(IPAddress.Loopback, _server.Port), new(IPAddress.Loopback, second.Port)];
        byte[]? answer = await client.ExchangeAsync(servers, query, 1, 1, false, Ct);
        Assert.Equal("example.org          A        192.0.2.9", ZabbixDnsClient.Records(answer!));

        Assert.Equal("0", await ValueAsync("net.dns[,example.org,A,1,1]"));
        Assert.True(double.Parse(await ValueAsync("net.dns.perf[,example.org,A,1,1]"), CultureInfo.InvariantCulture) > 0);
        Assert.Equal("The DNS query has no answer to report.", await ErrorAsync("net.dns.record[,example.org,A,1,1]"));
    }

    [Fact]
    public async Task With_no_servers_named_it_asks_this_host()
    {
        File.Delete(Path.Combine(_root, "etc", "resolv.conf"));
        _server.Udp = query => Reply(query, 0, false, Record(A, [192, 0, 2, 1]));
        Assert.Equal("1", await ValueAsync("net.dns[,example.org,A]"));
    }

    [Theory]
    [InlineData("net.dns[,]", "The second parameter must name what to ask about.")]
    [InlineData("net.dns[,example.org,QQ]", "The key's third parameter is not valid.")]
    [InlineData("net.dns[,example.org,A,x]", "The key's fourth parameter is not valid.")]
    [InlineData("net.dns[,example.org,A,0]", "The key's fourth parameter is not valid.")]
    [InlineData("net.dns[,example.org,A,1,x]", "The key's fifth parameter is not valid.")]
    [InlineData("net.dns[,example.org,A,1,0]", "The key's fifth parameter is not valid.")]
    [InlineData("net.dns[,example.org,A,1,1,sctp]", "The key's sixth parameter is not valid.")]
    [InlineData("net.dns[,example.org,A,1,1,tcp,extra]", "The key has more parameters than it takes.")]
    [InlineData("net.dns[not-an-address,example.org,A]", "The first parameter is not an IP address.")]
    [InlineData("net.dns[,a..b,A]", "The name cannot be asked about: a label is empty or longer than 63 bytes.")]
    public async Task A_bad_parameter_is_refused(string key, string reason) => Assert.Equal(reason, await ErrorAsync(key));

    [Fact]
    public async Task An_answer_that_cannot_be_read_says_so()
    {
        _server.Udp = query => Reply(query, 0, false, Record(Ns, [0xC0, 0xFF]));
        Assert.Equal("The DNS answer cannot be read: its name server name does not expand.", await ErrorAsync("net.dns.record[,example.org,NS]"));
        _server.Udp = query => Reply(query, 0, false, Record(A, [1, 2, 3, 4], owner: [0xC0, 0x40]));
        Assert.Equal("The DNS answer cannot be read: its domain name does not expand.", await ErrorAsync("net.dns.record[,example.org,A]"));
        _server.Udp = query =>
        {
            byte[] overflowing = Reply(query, 0, false, Record(A, [1, 2, 3, 4]));
            BinaryPrimitives.WriteUInt16BigEndian(overflowing.AsSpan(overflowing.Length - 6), 200);
            return overflowing;
        };
        Assert.Equal("The DNS answer cannot be read: a record runs past its end.", await ErrorAsync("net.dns.record[,example.org,A]"));
        _server.Udp = query => Reply(query, 0, false, Record(Wks, [1, 2, 3]));
        Assert.Equal("The DNS answer cannot be read: a WKS record is too short.", await ErrorAsync("net.dns.record[,example.org,WKS]"));
    }

    [Fact]
    public async Task A_name_that_points_at_itself_ends()
    {
        // The record's name is at offset 12 + the question: a pointer to that very place.
        _server.Udp = query => Reply(query, 0, false, Record(A, [1, 2, 3, 4], owner: [(byte)(0xC0 | (query.Length >> 8)), (byte)query.Length]));
        Assert.Equal("The DNS answer cannot be read: its domain name does not expand.", await ErrorAsync("net.dns.record[,example.org,A]"));
    }

    private sealed class FakeServer : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly TcpListener _tcp;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _loops = [];
        private IPEndPoint? _peer;
        private int _udpQuestions;
        private int _tcpQuestions;

        public FakeServer()
        {
            // The same port for both, as a DNS server has: a port UDP has taken may be TCP's in use, so it tries again.
            while (true)
            {
                _tcp = new TcpListener(IPAddress.Loopback, 0);
                _tcp.Start();
                Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
                try
                {
                    _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
                    break;
                }
                catch (SocketException)
                {
                    _tcp.Dispose();
                }
            }
            _loops.Add(ServeUdpAsync());
            _loops.Add(ServeTcpAsync());
        }

        public int Port { get; }

        public Func<byte[], byte[]?> Udp { get; set; } = _ => null;

        public Func<byte[], byte[]?> Tcp { get; set; } = _ => null;

        public byte[]? LastQuestion { get; private set; }

        /// <summary>What broke the test's own server: the test fails with this, not with a silence.</summary>
        public Exception? Failure => _loops.Find(loop => loop.IsFaulted)?.Exception;

        public int UdpQuestions => Volatile.Read(ref _udpQuestions);

        public int TcpQuestions => Volatile.Read(ref _tcpQuestions);

        public void SendUdp(byte[] message) => _udp.Send(message, message.Length, _peer);

        private async Task ServeUdpAsync()
        {
            try
            {
                while (true)
                {
                    UdpReceiveResult received = await _udp.ReceiveAsync(_stop.Token);
                    _peer = received.RemoteEndPoint;
                    LastQuestion = received.Buffer;
                    Interlocked.Increment(ref _udpQuestions);
                    if (Udp(received.Buffer) is { } reply) await _udp.SendAsync(reply, reply.Length, received.RemoteEndPoint);
                }
            }
            catch (Exception stopped) when (stopped is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // The test is over.
            }
        }

        private async Task ServeTcpAsync()
        {
            try
            {
                while (true)
                {
                    using TcpClient client = await _tcp.AcceptTcpClientAsync(_stop.Token);
                    NetworkStream stream = client.GetStream();
                    byte[] length = new byte[2];
                    await stream.ReadExactlyAsync(length, _stop.Token);
                    byte[] query = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
                    await stream.ReadExactlyAsync(query, _stop.Token);
                    LastQuestion = query;
                    Interlocked.Increment(ref _tcpQuestions);
                    if (Tcp(query) is not { } reply) continue;
                    BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)reply.Length);
                    await stream.WriteAsync(length, _stop.Token);
                    await stream.WriteAsync(reply, _stop.Token);
                }
            }
            catch (Exception stopped) when (stopped is OperationCanceledException or ObjectDisposedException or SocketException or IOException or InvalidOperationException)
            {
                // The test is over.
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _udp.Dispose();
            _tcp.Dispose();
            Task.WaitAll([.. _loops], TimeSpan.FromSeconds(5));
            _stop.Dispose();
        }
    }
}
