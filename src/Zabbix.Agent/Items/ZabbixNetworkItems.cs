// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Community.Zabbix.Items;

/// <summary>
/// Provides Zabbix's network keys: the interfaces and their counters (<c>net.if.*</c>), the sockets the host listens on
/// and holds (<c>net.tcp.listen</c>, <c>net.udp.listen</c>, <c>net.tcp.socket.count</c>, <c>net.udp.socket.count</c>),
/// whether a port or a service answers and how fast (<c>net.tcp.port</c>, <c>net.tcp.service</c>,
/// <c>net.udp.service</c>), name resolution (<c>net.dns</c>) and a Zabbix server's own statistics (<c>zabbix.stats</c>).
/// </summary>
/// <remarks>
/// On Linux the interface counters are <c>/proc/net/dev</c>'s, every mode Zabbix's agent answers; elsewhere they are
/// <see cref="IPInterfaceStatistics"/>'s bytes, packets, errors and drops. The service checks speak each protocol's
/// greeting as Zabbix's agent does (<c>ldap</c> excepted: the framework has no LDAP client). <c>net.dns</c> asks a DNS
/// server itself, the one the key names or the ones the system is configured with, over UDP or TCP, for every record
/// type Zabbix's agent names.
/// </remarks>
public sealed partial class ZabbixNetworkItems : ZabbixItemProvider
{
    // RFC 4253 §4.2: SSH-protoversion-softwareversion, as check_ssh's sscanf reads it.
    [System.Text.RegularExpressions.GeneratedRegex(@"^SSH-(\d+)\.(\d+)-\S", System.Text.RegularExpressions.RegexOptions.CultureInvariant, 1000)]
    private static partial System.Text.RegularExpressions.Regex SshIdentification();

    private static readonly FrozenDictionary<string, int> s_servicePorts = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["ssh"] = 22,
        ["ldap"] = 389,
        ["smtp"] = 25,
        ["ftp"] = 21,
        ["http"] = 80,
        ["pop"] = 110,
        ["nntp"] = 119,
        ["imap"] = 143,
        ["https"] = 443,
        ["telnet"] = 23,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly ZabbixItemSet _set = new();
    private readonly ZabbixHostFiles _files;
    private readonly TimeProvider _time;

    /// <summary>The network keys of the host this process runs on.</summary>
    /// <param name="time">The clock the <c>.perf</c> keys time a check on; the system clock by default.</param>
    public ZabbixNetworkItems(TimeProvider? time = null)
        : this(ZabbixHostFiles.Host, time ?? TimeProvider.System)
    {
    }

    internal ZabbixNetworkItems(ZabbixHostFiles files, TimeProvider time)
    {
        _files = files;
        _time = time;
        _set.Add("net.if.discovery", "The network interfaces, for low-level discovery", _ => InterfaceDiscovery())
            .Add("net.if.list", "The network interfaces, their type, state, address and description", _ => InterfaceList())
            .Add("net.if.in", "An interface's incoming traffic", key => Interface(key, Direction.In), "net.if.in[lo,bytes]")
            .Add("net.if.out", "An interface's outgoing traffic", key => Interface(key, Direction.Out), "net.if.out[lo,bytes]")
            .Add("net.if.total", "An interface's traffic, in and out", key => Interface(key, Direction.Total), "net.if.total[lo,bytes]")
            .Add("net.if.collisions", "An interface's collisions", Collisions, "net.if.collisions[lo]")
            .Add("net.tcp.listen", "Whether a TCP port is listened on", key => Listen(key, tcp: true), "net.tcp.listen[80]")
            .Add("net.udp.listen", "Whether a UDP port is listened on", key => Listen(key, tcp: false), "net.udp.listen[68]")
            .Add("net.tcp.socket.count", "The TCP sockets that match", TcpSocketCount, "net.tcp.socket.count[,80]")
            .Add("net.udp.socket.count", "The UDP sockets that match", UdpSocketCount, "net.udp.socket.count[,68]")
            .Add("net.tcp.port", "Whether a TCP connection to a port can be made", PortAsync, "net.tcp.port[,80]")
            .Add("net.tcp.service", "Whether a service answers on TCP", (key, token) => ServiceAsync(key, perf: false, token), "net.tcp.service[ssh,127.0.0.1,22]")
            .Add("net.tcp.service.perf", "How fast a service answers on TCP, in seconds", (key, token) => ServiceAsync(key, perf: true, token), "net.tcp.service.perf[ssh,127.0.0.1,22]")
            .Add("net.udp.service", "Whether a service answers on UDP", (key, token) => UdpServiceAsync(key, perf: false, token), "net.udp.service[ntp,127.0.0.1,123]")
            .Add("net.udp.service.perf", "How fast a service answers on UDP, in seconds", (key, token) => UdpServiceAsync(key, perf: true, token), "net.udp.service.perf[ntp,127.0.0.1,123]")
            .Add("net.dns", "Whether a DNS query succeeds", (key, token) => DnsAsync(key, DnsAnswer.Status, token), "net.dns[,localhost,A]")
            .Add("net.dns.perf", "How fast a DNS query is answered, in seconds", (key, token) => DnsAsync(key, DnsAnswer.Time, token), "net.dns.perf[,localhost,A]")
            .Add("net.dns.record", "The records a DNS query answers", (key, token) => DnsAsync(key, DnsAnswer.Records, token), "net.dns.record[,localhost,A]")
            .Add("zabbix.stats", "A Zabbix server's or proxy's internal statistics, as JSON", StatsAsync, "zabbix.stats[127.0.0.1,10051]");
    }

    /// <inheritdoc/>
    public override IReadOnlyList<ZabbixItemDefinition> Items => _set.Items;

    /// <inheritdoc/>
    public override ValueTask<ZabbixAgentAnswer> GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken = default) => _set.AnswerAsync(key, cancellationToken);

    private enum Direction
    {
        In,
        Out,
        Total,
    }

    private enum DnsAnswer
    {
        Status,
        Time,
        Records,
    }

    // /proc/net/dev: name, then 8 receive and 8 transmit counters (bytes packets errs drop fifo frame compressed multicast |
    // bytes packets errs drop fifo colls carrier compressed).
    private Dictionary<string, ulong[]> ProcNetDev()
    {
        var interfaces = new Dictionary<string, ulong[]>(StringComparer.Ordinal);
        foreach (string line in _files.ReadLines("/proc/net/dev"))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) continue;
            string[] fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 16) continue;
            interfaces[line[..colon].Trim()] = [.. fields.Take(16).Select(field => ulong.Parse(field, NumberStyles.None, CultureInfo.InvariantCulture))];
        }
        return interfaces;
    }

    private string InterfaceDiscovery()
    {
        IEnumerable<string> names = _files.IsLinux ? ProcNetDev().Keys : NetworkInterface.GetAllNetworkInterfaces().Select(adapter => adapter.Name);
        return ZabbixItemSet.Json(json =>
        {
            json.WriteStartArray();
            foreach (string name in names)
            {
                json.WriteStartObject();
                json.WriteString("{#IFNAME}", name);
                json.WriteEndObject();
            }
            json.WriteEndArray();
        });
    }

    // net_if_list (win32/net.c): one line per interface, its type, state, first IPv4 address and description.
    private static string InterfaceList()
    {
        if (!OperatingSystem.IsWindows()) throw ZabbixItemException.NotOnThisSystem();
        var lines = new List<string>();
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            string address = adapter.GetIPProperties().UnicastAddresses.FirstOrDefault(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "0.0.0.0";
            string state = adapter.OperationalStatus == OperationalStatus.Up ? "enabled" : "disabled";
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{adapter.NetworkInterfaceType,-8} {state,-8} {address,-15} {adapter.Description}"));
        }
        return string.Join('\n', lines);
    }

    private string Interface(ZabbixItemKey key, Direction direction)
    {
        ZabbixItemSet.AtMost(key, 2);
        string name = ZabbixItemSet.Parameter(key, 0);
        if (name.Length == 0) throw ZabbixItemException.InvalidParameter(0);
        string mode = ZabbixItemSet.Parameter(key, 1);
        if (mode.Length == 0) mode = "bytes";
        if (_files.IsLinux)
        {
            if (!ProcNetDev().TryGetValue(name, out ulong[]? c)) throw new ZabbixItemException("/proc/net/dev does not list this network interface.");
            ulong? value = direction switch
            {
                Direction.In => mode switch
                {
                    "bytes" => c[0],
                    "packets" => c[1],
                    "errors" => c[2],
                    "dropped" => c[3],
                    "overruns" or "fifo" => c[4],
                    "frame" => c[5],
                    "compressed" => c[6],
                    "multicast" => c[7],
                    _ => null,
                },
                Direction.Out => mode switch
                {
                    "bytes" => c[8],
                    "packets" => c[9],
                    "errors" => c[10],
                    "dropped" => c[11],
                    "overruns" or "fifo" => c[12],
                    "collisions" or "colls" => c[13],
                    "carrier" => c[14],
                    "compressed" => c[15],
                    _ => null,
                },
                _ => mode switch
                {
                    "bytes" => c[0] + c[8],
                    "packets" => c[1] + c[9],
                    "errors" => c[2] + c[10],
                    "dropped" => c[3] + c[11],
                    "overruns" or "fifo" => c[4] + c[12],
                    "compressed" => c[6] + c[15],
                    _ => null,
                },
            };
            return ZabbixItemSet.Integer(value ?? throw ZabbixItemException.InvalidParameter(1));
        }
        if (OperatingSystem.IsLinux()) throw ZabbixItemException.NotOnThisSystem();       // Linux's counters are /proc/net/dev's, above
        NetworkInterface adapter = Adapter(name);
        IPInterfaceStatistics s = adapter.GetIPStatistics();
        (long In, long Out) pair = mode switch
        {
            "bytes" => (s.BytesReceived, s.BytesSent),
            "packets" => (s.UnicastPacketsReceived + s.NonUnicastPacketsReceived, s.UnicastPacketsSent + s.NonUnicastPacketsSent),
            "errors" => (s.IncomingPacketsWithErrors, s.OutgoingPacketsWithErrors),
            "dropped" when !OperatingSystem.IsMacOS() => (s.IncomingPacketsDiscarded, s.OutgoingPacketsDiscarded),
            _ => throw (mode is "dropped" or "overruns" or "fifo" or "frame" or "compressed" or "multicast" or "collisions" or "colls" or "carrier"
                ? ZabbixItemException.NotOnThisSystem()
                : ZabbixItemException.InvalidParameter(1)),
        };
        return ZabbixItemSet.Integer(direction switch { Direction.In => pair.In, Direction.Out => pair.Out, _ => pair.In + pair.Out });
    }

    // Windows names an interface by its description, its IPv4 address or its {GUID} as well as its name.
    private static NetworkInterface Adapter(string name)
        => NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(adapter => adapter.Name == name || adapter.Description == name || adapter.Id == name
                || adapter.GetIPProperties().UnicastAddresses.Any(unicast => unicast.Address.ToString() == name))
            ?? throw new ZabbixItemException("This host has no network interface by that name.");

    private string Collisions(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 1);
        string name = ZabbixItemSet.Parameter(key, 0);
        if (name.Length == 0) throw ZabbixItemException.InvalidParameter(0);
        if (!_files.IsLinux) throw ZabbixItemException.NotOnThisSystem();
        return ProcNetDev().TryGetValue(name, out ulong[]? counters)
            ? ZabbixItemSet.Integer(counters[13])
            : throw new ZabbixItemException("/proc/net/dev does not list this network interface.");
    }

    private static string Listen(ZabbixItemKey key, bool tcp)
    {
        ZabbixItemSet.AtMost(key, 1);
        if (!ushort.TryParse(ZabbixItemSet.Parameter(key, 0), NumberStyles.None, CultureInfo.InvariantCulture, out ushort port)) throw ZabbixItemException.InvalidParameter(0);
        IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
        IPEndPoint[] listeners = tcp ? properties.GetActiveTcpListeners() : properties.GetActiveUdpListeners();
        return listeners.Any(endpoint => endpoint.Port == port) ? "1" : "0";
    }

    // net_tcp_socket_count: local address, local port, remote address, remote port, state.
    private string TcpSocketCount(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 5);
        AddressFilter local = AddressFilter.Parse(ZabbixItemSet.Parameter(key, 0), 0), remote = AddressFilter.Parse(ZabbixItemSet.Parameter(key, 2), 2);
        int? localPort = Port(key, 1, tcp: true), remotePort = Port(key, 3, tcp: true);
        string stateText = ZabbixItemSet.Parameter(key, 4);
        TcpState? state = stateText.Length == 0 ? null : stateText switch
        {
            "established" => TcpState.Established,
            "syn_sent" => TcpState.SynSent,
            "syn_recv" => TcpState.SynReceived,
            "fin_wait1" => TcpState.FinWait1,
            "fin_wait2" => TcpState.FinWait2,
            "time_wait" => TcpState.TimeWait,
            "close" => TcpState.Closed,
            "close_wait" => TcpState.CloseWait,
            "last_ack" => TcpState.LastAck,
            "listen" => TcpState.Listen,
            "closing" => TcpState.Closing,
            _ => throw ZabbixItemException.InvalidParameter(4),
        };
        if (OperatingSystem.IsMacOS()) throw ZabbixItemException.NotOnThisSystem();
        IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
        IEnumerable<(IPEndPoint Local, IPEndPoint? Remote, TcpState State)> sockets =
            properties.GetActiveTcpConnections().Select(connection => (connection.LocalEndPoint, (IPEndPoint?)connection.RemoteEndPoint, connection.State))
                .Concat(properties.GetActiveTcpListeners().Select(listener => (listener, (IPEndPoint?)null, TcpState.Listen)));
        int count = sockets.Count(socket => (state is null || socket.State == state)
            && local.Matches(socket.Local.Address) && (localPort is null || socket.Local.Port == localPort)
            && (remote.Any || (socket.Remote is { } r && remote.Matches(r.Address))) && (remotePort is null || socket.Remote?.Port == remotePort));
        return ZabbixItemSet.Integer(count);
    }

    // net_udp_socket_count: the framework lists UDP sockets by their local end only, so all are unconnected.
    private string UdpSocketCount(ZabbixItemKey key)
    {
        ZabbixItemSet.AtMost(key, 5);
        AddressFilter local = AddressFilter.Parse(ZabbixItemSet.Parameter(key, 0), 0), remote = AddressFilter.Parse(ZabbixItemSet.Parameter(key, 2), 2);
        int? localPort = Port(key, 1, tcp: false), remotePort = Port(key, 3, tcp: false);
        string state = ZabbixItemSet.Parameter(key, 4);
        if (state.Length > 0 && state is not ("established" or "unconn")) throw ZabbixItemException.InvalidParameter(4);
        if (OperatingSystem.IsMacOS() || !remote.Any || remotePort is not null || state == "established") throw ZabbixItemException.NotOnThisSystem();
        int count = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Count(endpoint => local.Matches(endpoint.Address) && (localPort is null || endpoint.Port == localPort));
        return ZabbixItemSet.Integer(count);
    }

    // A port number, or a service name from the services database (/etc/services).
    private int? Port(ZabbixItemKey key, int index, bool tcp)
    {
        string text = ZabbixItemSet.Parameter(key, index);
        if (text.Length == 0) return null;
        if (ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ushort port)) return port;
        string services = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "services") : "/etc/services";
        string? database = OperatingSystem.IsWindows() ? (File.Exists(services) ? File.ReadAllText(services) : null) : _files.TryReadAllText(services);
        foreach (string line in database?.Split('\n') ?? [])
        {
            string[] fields = line.Split('#')[0].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !fields.Skip(2).Prepend(fields[0]).Contains(text, StringComparer.Ordinal)) continue;
            string[] entry = fields[1].Split('/');
            if (entry.Length == 2 && entry[1] == (tcp ? "tcp" : "udp") && ushort.TryParse(entry[0], NumberStyles.None, CultureInfo.InvariantCulture, out port)) return port;
        }
        throw ZabbixItemException.InvalidParameter(index);
    }

    /// <summary>An address, a CIDR block, or nothing (every address).</summary>
    private sealed class AddressFilter
    {
        private readonly IPNetwork? _network;

        private AddressFilter(IPNetwork? network) => _network = network;

        public bool Any => _network is null;

        // An address or a CIDR block, read as a ZabbixPeer reads one; a name is not an address here.
        public static AddressFilter Parse(string text, int index)
        {
            if (text.Length == 0) return new AddressFilter(null);
            return ZabbixPeer.TryParse(text, out ZabbixPeer peer) && peer.Network is { } network
                ? new AddressFilter(network)
                : throw ZabbixItemException.InvalidParameter(index);
        }

        public bool Matches(IPAddress address)
        {
            if (_network is not { } network) return true;
            if (address.IsIPv4MappedToIPv6 && network.BaseAddress.AddressFamily == AddressFamily.InterNetwork) address = address.MapToIPv4();
            return network.Contains(address);
        }
    }

    private async ValueTask<string> PortAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        ZabbixItemSet.AtMost(key, 2);
        string host = ZabbixItemSet.Parameter(key, 0) is { Length: > 0 } ip ? ip : "127.0.0.1";
        if (!ushort.TryParse(ZabbixItemSet.Parameter(key, 1), NumberStyles.None, CultureInfo.InvariantCulture, out ushort port)) throw ZabbixItemException.InvalidParameter(1);
        using TcpClient? client = await ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        return client is null ? "0" : "1";
    }

    // zbx_check_service_default_addr (simple/simple.c): the service, its address (127.0.0.1) and its port.
    private async ValueTask<string> ServiceAsync(ZabbixItemKey key, bool perf, CancellationToken cancellationToken)
    {
        long started = _time.GetTimestamp();
        ZabbixItemSet.AtMost(key, 3);
        string service = ZabbixItemSet.Parameter(key, 0);
        if (service.Length == 0 || (service != "tcp" && !s_servicePorts.ContainsKey(service))) throw ZabbixItemException.InvalidParameter(0);
        string host = ZabbixItemSet.Parameter(key, 1) is { Length: > 0 } ip ? ip : "127.0.0.1";
        string portText = ZabbixItemSet.Parameter(key, 2);
        ushort port;
        if (portText.Length == 0)
        {
            if (service == "tcp") throw ZabbixItemException.InvalidParameter(2);
            port = (ushort)s_servicePorts[service];
        }
        else if (!ushort.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port))
        {
            throw ZabbixItemException.InvalidParameter(2);
        }
        if (service == "ldap") throw new ZabbixItemException("The SDK has no LDAP client to check with.");
        bool up = await CheckServiceAsync(service, host, port, cancellationToken).ConfigureAwait(false);
        return Result(up, perf, started);
    }

    private string Result(bool up, bool perf, long started)
    {
        if (!perf) return up ? "1" : "0";
        if (!up) return ZabbixItemSet.Float(0);
        double seconds = _time.GetElapsedTime(started).TotalSeconds;
        return ZabbixItemSet.Float(Math.Max(seconds, double.Epsilon));
    }

    private static async Task<bool> CheckServiceAsync(string service, string host, ushort port, CancellationToken cancellationToken)
    {
        using TcpClient? client = await ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        if (client is null) return false;
        NetworkStream stream = client.GetStream();
        try
        {
            switch (service)
            {
                case "tcp":
                case "http":
                    return true;
                case "https":
                    {
                        // check_https sets CURLOPT_SSL_VERIFYPEER and CURLOPT_SSL_VERIFYHOST to 0: the check is that TLS is
                        // spoken, not who the certificate names.
#pragma warning disable CA5359
                        var tls = new SslStream(stream, leaveInnerStreamOpen: true, static (_, _, _, _) => true);
#pragma warning restore CA5359
                        await using (tls.ConfigureAwait(false))
                            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cancellationToken).ConfigureAwait(false);
                        return true;
                    }
                case "ssh":
                    return await SshAsync(stream, cancellationToken).ConfigureAwait(false);
                case "telnet":
                    return await TelnetAsync(stream, cancellationToken).ConfigureAwait(false);
                default:
                    return await ExpectAsync(stream, service, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception refused) when (refused is IOException or SocketException or System.Security.Authentication.AuthenticationException)
        {
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;                                                               // tcp_expect: no answer within the check's timeout is down
        }
    }

    // tcp_expect with each protocol's validator; the closing command is sent when the greeting was right.
    private static async Task<bool> ExpectAsync(NetworkStream stream, string service, CancellationToken cancellationToken)
    {
        var reader = new LineReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            bool? verdict = service switch
            {
                "smtp" => line.StartsWith("220", StringComparison.Ordinal) ? (line.Length > 3 && line[3] == '-' ? null : line.Length == 3 || line[3] == ' ') : false,
                "ftp" => line.StartsWith("220 ", StringComparison.Ordinal) ? true : null,
                "pop" => line.StartsWith("+OK", StringComparison.Ordinal),
                "nntp" => line.StartsWith("200", StringComparison.Ordinal) || line.StartsWith("201", StringComparison.Ordinal),
                _ => line.StartsWith("* OK", StringComparison.Ordinal),
            };
            if (verdict is null) continue;
            if (verdict.Value) await stream.WriteAsync(Encoding.ASCII.GetBytes(service == "imap" ? "a1 LOGOUT\r\n" : "QUIT\r\n"), cancellationToken).ConfigureAwait(false);
            return verdict.Value;
        }
        return false;
    }

    // check_ssh: an RFC 4253 identification line, answered with the agent's own.
    private static async Task<bool> SshAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var reader = new LineReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            System.Text.RegularExpressions.Match version = SshIdentification().Match(line);
            if (!version.Success) continue;
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"SSH-{version.Groups[1].Value}.{version.Groups[2].Value}-zabbix_agent\r\n"), cancellationToken).ConfigureAwait(false);
            return true;
        }
        await stream.WriteAsync("0\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        return false;
    }

    // zbx_telnet_test_login: refuse every option the server offers or asks for, until it prompts for a login (':').
    private static async Task<bool> TelnetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        const byte Iac = 255, Will = 251, Wont = 252, Do = 253, Dont = 254;
        byte[] buffer = new byte[1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == Iac && i + 2 < read && buffer[i + 1] is Will or Wont or Do or Dont)
                {
                    byte reply = buffer[i + 1] is Do or Dont ? Wont : Dont;
                    await stream.WriteAsync(new byte[] { Iac, reply, buffer[i + 2] }, cancellationToken).ConfigureAwait(false);
                    i += 2;
                }
                else if (buffer[i] == (byte)':')
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static async Task<TcpClient?> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient();       // both address families where the host has IPv6, IPv4 where it does not
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch (SocketException)
        {
            client.Dispose();                                                           // refused
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();                                                           // no answer within the check's timeout
            return null;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // check_ntp: a client request; the answer is a server's (mode 4) with a stratum and a transmit time.
    private async ValueTask<string> UdpServiceAsync(ZabbixItemKey key, bool perf, CancellationToken cancellationToken)
    {
        long started = _time.GetTimestamp();
        ZabbixItemSet.AtMost(key, 3);
        if (ZabbixItemSet.Parameter(key, 0) != "ntp") throw ZabbixItemException.InvalidParameter(0);
        string host = ZabbixItemSet.Parameter(key, 1) is { Length: > 0 } ip ? ip : "127.0.0.1";
        string portText = ZabbixItemSet.Parameter(key, 2);
        ushort port = 123;
        if (portText.Length > 0 && !ushort.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)) throw ZabbixItemException.InvalidParameter(2);
        bool up;
        try
        {
            using var client = new UdpClient();
            client.Connect(host, port);
            byte[] request = new byte[48];
            request[0] = 0x23;                                                          // LI 0, version 4, mode 3 (client)
            await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            UdpReceiveResult reply = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            byte[] data = reply.Buffer;
            up = data.Length >= 48 && (data[0] & 0x07) == 4 && data[1] != 0 && data.AsSpan(40, 8).ContainsAnyExcept((byte)0);
        }
        catch (SocketException)
        {
            up = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            up = false;                                                                 // check_ntp: no answer within the check's timeout is down
        }
        return Result(up, perf, started);
    }

    /// <summary>The port DNS servers are asked on (<c>ZBX_DEFAULT_DNS_PORT</c>); a test's own server listens elsewhere.</summary>
    internal int DnsPort { get; init; } = 53;

    // dns_query (common/dns.c): ip, name, type, timeout, count, protocol.
    private async ValueTask<string> DnsAsync(ZabbixItemKey key, DnsAnswer answer, CancellationToken cancellationToken)
    {
        long started = _time.GetTimestamp();
        ZabbixItemSet.AtMost(key, 6);
        string server = ZabbixItemSet.Parameter(key, 0);
        string name = ZabbixItemSet.Parameter(key, 1);
        if (name.Length == 0) throw new ZabbixItemException("The second parameter must name what to ask about.");
        string typeName = ZabbixItemSet.Parameter(key, 2);
        ushort type = typeName.Length == 0 ? (ushort)6 : ZabbixDnsClient.TypeNumber(typeName) ?? throw ZabbixItemException.InvalidParameter(2);
        int seconds = DnsCount(key, 3, 1), tries = DnsCount(key, 4, 2);
        bool tcp = ZabbixItemSet.Mode(key, 5, "udp", "tcp") == "tcp";
        if (type == ZabbixDnsClient.Ptr) name = ZabbixDnsClient.Reversed(name);
        byte[] query = ZabbixDnsClient.Query(name, type);
        IReadOnlyList<IPEndPoint> servers = DnsServers(server);
        byte[]? response = await new ZabbixDnsClient(_time).ExchangeAsync(servers, query, seconds, tries, tcp, cancellationToken).ConfigureAwait(false);
        bool up = response is not null && ZabbixDnsClient.ResponseCode(response) == 0 && ZabbixDnsClient.AnswerCount(response) != 0;
        return answer switch
        {
            DnsAnswer.Status => up ? "1" : "0",
            // Any answer has a time, a refusal's too; only no answer at all is 0.
            DnsAnswer.Time => ZabbixItemSet.Float(response is null ? 0 : Math.Max(_time.GetElapsedTime(started).TotalSeconds, double.Epsilon)),
            _ => response is not null && up ? ZabbixDnsClient.Records(response) : throw new ZabbixItemException("The DNS query has no answer to report."),
        };
    }

    private static int DnsCount(ZabbixItemKey key, int index, int absent)
    {
        string text = ZabbixItemSet.Parameter(key, index);
        if (text.Length == 0) return absent;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value != 0 ? value : throw ZabbixItemException.InvalidParameter(index);
    }

    // res_ninit: /etc/resolv.conf's first three, or this host when it names none. Windows' are its interfaces'.
    private List<IPEndPoint> DnsServers(string named)
    {
        if (named.Length != 0)
            return IPAddress.TryParse(named, out IPAddress? address) ? [new IPEndPoint(address, DnsPort)] : throw new ZabbixItemException("The first parameter is not an IP address.");
        var servers = new List<IPEndPoint>();
        if (!_files.IsLinux && OperatingSystem.IsWindows())
        {
            foreach (IPAddress address in NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .SelectMany(adapter => adapter.GetIPProperties().DnsAddresses)
                .Where(address => !address.IsIPv6SiteLocal)
                .Distinct())
            {
                servers.Add(new IPEndPoint(address, DnsPort));
            }
            return servers;
        }
        const int MostServers = 3;      // MAXNS
        foreach (string line in _files.TryReadAllText("/etc/resolv.conf")?.Split('\n') ?? [])
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && fields[0] == "nameserver" && servers.Count < MostServers && IPAddress.TryParse(fields[1], out IPAddress? address))
                servers.Add(new IPEndPoint(address, DnsPort));
        }
        if (servers.Count == 0) servers.Add(new IPEndPoint(IPAddress.Loopback, DnsPort));
        return servers;
    }

    // zabbix_stats (common/zabbix_stats.c): the server's or proxy's reply, as it sent it.
    private static async ValueTask<string> StatsAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        ZabbixItemSet.AtMost(key, 5);
        string host = ZabbixItemSet.Parameter(key, 0) is { Length: > 0 } ip ? ip : "127.0.0.1";
        string portText = ZabbixItemSet.Parameter(key, 1);
        int port = ZabbixServerNode.DefaultPort;
        if (portText.Length > 0)
            port = ushort.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out ushort parsed) ? parsed : throw ZabbixItemException.InvalidParameter(1);
        bool queue = key.Parameters.Count >= 3;
        if (queue && ZabbixItemSet.Parameter(key, 2) != "queue") throw ZabbixItemException.InvalidParameter(2);
        TimeSpan? from = queue ? Delay(key, 3) : null, to = queue ? Delay(key, 4) : null;
        using var wire = new ZabbixServerWire(new ZabbixServerNode(host, port));
        try
        {
            string data;
            if (queue)
            {
                long size = await wire.GetQueueSizeAsync(from, to, cancellationToken).ConfigureAwait(false);
                data = string.Create(CultureInfo.InvariantCulture, $$"""{"queue":{{size}}}""");
            }
            else
            {
                data = (await wire.GetStatsAsync(cancellationToken).ConfigureAwait(false)).GetRawText();
            }
            return $$"""{"response":"success","data":{{data}}}""";
        }
        catch (ZabbixServerRefusedException refused)
        {
            throw new ZabbixItemException($"The server's statistics cannot be read: {refused.Info ?? refused.Message}", refused);
        }
        catch (Exception failed) when (failed is IOException or SocketException or ZabbixWireException)
        {
            throw new ZabbixItemException($"The server's statistics cannot be read: {failed.Message}", failed);
        }

        static TimeSpan? Delay(ZabbixItemKey key, int index)
        {
            string text = ZabbixItemSet.Parameter(key, index);
            if (text.Length == 0) return null;
            return ZabbixItemSet.Duration(text) ?? throw ZabbixItemException.InvalidParameter(index);
        }
    }

    /// <summary>Reads lines from a stream as <c>zbx_tcp_recv_line</c> does: up to a newline, without the CR.</summary>
    private sealed class LineReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[4096];
        private int _start;
        private int _end;

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new List<byte>();
            while (true)
            {
                if (_start == _end)
                {
                    _start = 0;
                    _end = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                    if (_end == 0) return line.Count == 0 ? null : Encoding.ASCII.GetString([.. line]);
                }
                byte b = _buffer[_start++];
                if (b == (byte)'\n') return Encoding.ASCII.GetString([.. line]).TrimEnd('\r');
                line.Add(b);
            }
        }
    }
}
