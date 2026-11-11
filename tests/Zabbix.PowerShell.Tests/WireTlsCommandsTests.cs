// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Community.Zabbix.PowerShell.Commands;
using Community.Zabbix.Proxy;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// TLS on every wire command: one parameter shape (<see cref="ZabbixTlsCmdlet"/>, <see cref="ZabbixWireCmdlet"/>,
/// <see cref="ZabbixServerWireCmdlet"/>), the connection's trapper TLS inherited by the server's own protocol, the
/// pre-shared key over a PSK-only fake trapper, certificates end to end through a passive agent, and the key kept out of
/// every stream.
/// </summary>
public sealed class WireTlsCommandsTests : IDisposable
{
    private const string Identity = "zbx-psk";
    private const string Key = "1f87b595725ac58dd977beef14b97461a7c1045b9a1c963065002c5473194952";

    // Every command that connects over a framed protocol, and every listener.
    private static readonly Type[] s_connecting =
    [
        typeof(SendZbxDataCommand), typeof(GetZbxAgentItemCommand), typeof(GetZbxQueueCommand), typeof(GetZbxServerStatusCommand),
        typeof(TestZbxItemCommand), typeof(TestZbxExpressionCommand), typeof(TestZbxMediaTypeCommand), typeof(ConnectZbxServerCommand),
        typeof(SendZbxProxyDataCommand), typeof(ReceiveZbxProxyConfigCommand), typeof(StartZbxActiveProxyCommand), typeof(StartZbxActiveAgentCommand),
    ];

    private static readonly Type[] s_listening = [typeof(StartZbxPassiveAgentCommand), typeof(StartZbxPassiveProxyCommand)];

    private static readonly string[] s_words =
        ["PskIdentity", "Psk", "Certificate", "CertificateKey", "CertificateAuthority", "CertificateRevocationList", "ServerIssuer", "ServerSubject"];

    private readonly string _directory = Directory.CreateTempSubdirectory("zbx-tls-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string KeyArgument => $"(ConvertTo-SecureString '{Key}' -AsPlainText -Force)";

    // ----- one shape on every command ----------------------------------------------------------------------

    public static TheoryData<Type> Connecting => [.. s_connecting];

    public static TheoryData<Type> Listening => [.. s_listening];

    public static TheoryData<Type> All => [.. s_connecting.Concat(s_listening)];

    // Connect-ZbxServer's subject is the API's HTTPS endpoint, so its words say they are the trapper's (-TrapperPsk).
    private static string Named(Type command, string word) => command == typeof(ConnectZbxServerCommand) ? "Trapper" + word : word;

    [Theory, MemberData(nameof(All))]
    public void Every_wire_command_takes_the_TLS_words_in_one_shape(Type command)
    {
        foreach (string word in s_words)
        {
            PropertyInfo reference = typeof(ZabbixTlsCmdlet).GetProperty(word)!;
            PropertyInfo property = command.GetProperty(Named(command, word)) ?? throw new Xunit.Sdk.XunitException($"{command.Name} has no -{Named(command, word)}");
            Assert.Equal(reference.PropertyType, property.PropertyType);
            Assert.Equal(Shape(reference), Shape(property));
        }
    }

    [Theory, MemberData(nameof(Connecting))]
    public void Every_connecting_command_takes_Tls_as_the_connection_s_mode(Type command)
    {
        PropertyInfo property = command.GetProperty(Named(command, "Tls")) ?? throw new Xunit.Sdk.XunitException($"{command.Name} has no -{Named(command, "Tls")}");
        Assert.Equal(typeof(ZabbixTlsMode?), property.PropertyType);
        Assert.Equal(Shape(typeof(ZabbixWireCmdlet).GetProperty("Tls")!), Shape(property));
        Assert.Null(command.GetProperty("TlsAccept"));
    }

    [Theory, MemberData(nameof(Listening))]
    public void Every_listener_takes_TlsAccept_and_no_Tls(Type command)
    {
        Assert.Equal(typeof(ZabbixTlsAcceptModes[]), command.GetProperty("TlsAccept")?.PropertyType);
        Assert.Null(command.GetProperty("Tls"));
    }

    // Optional, named, in every set, never from the pipeline, with the same validation and transformation.
    private static string Shape(PropertyInfo property)
    {
        ParameterAttribute parameter = Assert.Single(property.GetCustomAttributes<ParameterAttribute>());
        Assert.False(parameter.Mandatory, property.Name);
        Assert.Equal(int.MinValue, parameter.Position);
        Assert.False(parameter.ValueFromPipeline || parameter.ValueFromPipelineByPropertyName, property.Name);
        Assert.Equal(ParameterAttribute.AllParameterSets, parameter.ParameterSetName);
        return string.Join(",", property.GetCustomAttributes().Where(attribute => attribute is not ParameterAttribute)
            .Select(attribute => attribute.GetType().Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_help_names_every_TLS_word_on_a_server_wire_and_a_proxy_command()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            foreach ($name in 'Get-ZbxQueue', 'Send-ZbxProxyData', 'Start-ZbxPassiveAgent') {
                $help = Get-Help $name -Full
                foreach ($word in 'PskIdentity', 'Psk', 'Certificate', 'CertificateKey', 'CertificateAuthority', 'CertificateRevocationList', 'ServerIssuer', 'ServerSubject') {
                    $p = $help.parameters.parameter | Where-Object name -eq $word
                    '{0} {1} {2}' -f $name, $word, [bool]($p.description.Text -join '')
                }
            }
            """);

        Assert.Empty(run.Errors);
        Assert.All(run.Output, line => Assert.EndsWith("True", line.ToString(), StringComparison.Ordinal));
        Assert.Equal(24, run.Output.Count);
    }

    // ----- pre-shared key over the wire --------------------------------------------------------------------

    [Fact]
    public async Task Send_ZbxData_sends_over_a_pre_shared_key_given_on_the_command()
    {
        await using FakeTrapper trapper = FakeTrapper.StartPsk(Identity, Key);
        trapper.On("sender data", FakeTrapper.Success(1, 0));
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Send-ZbxData web-01 agent.ping 1 -Server 127.0.0.1:{trapper.Port} -PskIdentity {Identity} -Psk {KeyArgument} -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Equal(1, Assert.IsType<ZabbixSenderResponse>(Assert.Single(run.Output).BaseObject).Processed);
        Assert.Single(trapper.FramesOf("sender data"));
    }

    [Fact]
    public async Task Without_TLS_a_psk_only_trapper_refuses_the_connection()
    {
        await using FakeTrapper trapper = FakeTrapper.StartPsk(Identity, Key);
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Send-ZbxData web-01 agent.ping 1 -Server 127.0.0.1:{trapper.Port} -Confirm:$false");

        Assert.NotEmpty(run.Errors);
        Assert.Empty(trapper.Frames);
    }

    [Fact]
    public async Task The_connections_trapper_TLS_reaches_every_server_wire_command_given_none()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.StartPsk(Identity, Key);
        trapper.On("sender data", FakeTrapper.Success(1, 0));
        trapper.On("status.get", """{"response":"success","data":{"template stats":[]}}""");
        trapper.On("queue.get", """{"response":"success","data":[]}""");
        using Shell shell = Shell.Open();
        Run connected = shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port} -TrapperPskIdentity {Identity} -TrapperPsk {KeyArgument}");
        Assert.Empty(connected.Errors);

        Run sent = shell.Invoke("Send-ZbxData web-01 agent.ping 1 -Confirm:$false");
        Run status = shell.Invoke("Get-ZbxServerStatus");
        Run queue = shell.Invoke("Get-ZbxQueue");

        Assert.Empty(sent.Errors);
        Assert.Empty(status.Errors);
        Assert.Empty(queue.Errors);
        Assert.Single(trapper.FramesOf("sender data"));
        Assert.Single(trapper.FramesOf("status.get"));
        Assert.Single(trapper.FramesOf("queue.get"));
    }

    [Fact]
    public async Task A_command_s_own_TLS_words_replace_the_connection_s()
    {
        // The connection's trapper takes PSK; -Tls Unencrypted on the command replaces it for that call, so the same
        // trapper refuses the plain frame.
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.StartPsk(Identity, Key);
        trapper.On("status.get", """{"response":"success","data":{}}""");
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port} -TrapperPskIdentity {Identity} -TrapperPsk {KeyArgument}");

        Run plain = shell.Invoke("Get-ZbxServerStatus -Tls Unencrypted");

        Assert.NotEmpty(plain.Errors);
        Assert.Empty(trapper.FramesOf("status.get"));
    }

    [Fact]
    public async Task Send_ZbxData_to_another_server_does_not_take_the_connection_s_TLS()
    {
        // -Server may name a proxy's trapper, which the connection's words are not for: a plain trapper takes the value.
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper proxy = FakeTrapper.Start();
        proxy.On("sender data", FakeTrapper.Success(1, 0));
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:1 -TrapperPskIdentity {Identity} -TrapperPsk {KeyArgument}");

        Run run = shell.Invoke($"Send-ZbxData web-01 agent.ping 1 -Server 127.0.0.1:{proxy.Port} -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Single(proxy.FramesOf("sender data"));
    }

    [Fact]
    public async Task Psk_takes_a_key_file_relative_to_the_PowerShell_location()
    {
        await using FakeTrapper trapper = FakeTrapper.StartPsk(Identity, Key);
        trapper.On("sender data", FakeTrapper.Success(1, 0));
        await File.WriteAllTextAsync(Path.Combine(_directory, "agent.psk"), Key + "\n", TestContext.Current.CancellationToken);
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Set-Location '{_directory}'; Send-ZbxData web-01 agent.ping 1 -Server 127.0.0.1:{trapper.Port} -PskIdentity {Identity} -Psk agent.psk -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Single(trapper.FramesOf("sender data"));
    }

    [Fact]
    public void Psk_refuses_text_that_names_no_file_without_repeating_it()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Send-ZbxData web-01 agent.ping 1 -Server 127.0.0.1:1 -PskIdentity {Identity} -Psk {Key} -Confirm:$false");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("ParameterArgumentTransformationError", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Key, error.Exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_wrong_key_fails_and_no_stream_carries_either_key()
    {
        await using FakeTrapper trapper = FakeTrapper.StartPsk(Identity, Key);
        string wrong = new('a', 64);
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Send-ZbxData web-01 agent.ping 1 -Server 127.0.0.1:{trapper.Port} -PskIdentity {Identity} -Psk (ConvertTo-SecureString '{wrong}' -AsPlainText -Force) -Confirm:$false -Verbose -Debug");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.Empty(trapper.Frames);
        string everything = string.Join("\n", run.Verbose.Concat(run.Debug).Concat(run.Warnings)
            .Append(error.ToString()).Append(error.Exception.ToString()).Append(error.ErrorDetails?.Message ?? string.Empty));
        Assert.DoesNotContain(wrong, everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Key, everything, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Credentials_of_both_kinds_without_Tls_stop_the_command()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Get-ZbxAgentItem 127.0.0.1:1 agent.ping -PskIdentity {Identity} -Psk {KeyArgument} -Certificate c.pem -CertificateKey c.key -CertificateAuthority ca.pem");

        Assert.StartsWith("ZabbixTlsModeAmbiguous", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("psk")]
    [InlineData("PreSharedKey")]
    public async Task Tls_takes_Zabbix_s_words_and_the_member_names(string mode)
    {
        await using FakeTrapper agent = FakeTrapper.StartPsk(Identity, Key);
        agent.Otherwise = _ => "1";
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Get-ZbxAgentItem 127.0.0.1:{agent.Port} agent.ping -Tls {mode} -PskIdentity {Identity} -Psk {KeyArgument}");

        Assert.Empty(run.Errors);
        Assert.Equal("1", Assert.IsType<ZabbixAgentAnswer>(Assert.Single(run.Output).BaseObject).Value);
    }

    [Fact]
    public async Task The_proxy_uplink_commands_reach_a_psk_server()
    {
        await using FakeTrapper server = FakeTrapper.StartPsk(Identity, Key);
        server.On("proxy config", "{}");
        server.On("proxy data", """{"response":"success","info":"processed: 1; failed: 0; total: 1; seconds spent: 0.000100"}""");
        using Shell shell = Shell.Open();

        Run config = shell.Invoke($"Receive-ZbxProxyConfig zbx-proxy 127.0.0.1:{server.Port} -PskIdentity {Identity} -Psk {KeyArgument}");
        Run data = shell.Invoke($"New-ZbxHistoryValue -ItemId 42 -Value 7 | Send-ZbxProxyData zbx-proxy 127.0.0.1:{server.Port} -PskIdentity {Identity} -Psk {KeyArgument} -Confirm:$false");

        Assert.Empty(config.Errors);
        Assert.IsType<ZabbixProxyConfig>(Assert.Single(config.Output).BaseObject);
        Assert.Empty(data.Errors);
        Assert.True(Assert.IsType<ZabbixProxyResponse>(Assert.Single(data.Output).BaseObject).IsSuccess);
    }

    [Fact]
    public async Task Start_ZbxActiveProxy_pulls_its_configuration_over_psk()
    {
        await using FakeTrapper server = FakeTrapper.StartPsk(Identity, Key);
        TaskCompletionSource<string> pulled = server.ExpectArrival();
        server.On("proxy config", "{}");
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke($"Start-ZbxActiveProxy probe zbx-proxy 127.0.0.1:{server.Port} -PskIdentity {Identity} -Psk {KeyArgument} -Confirm:$false");

            Assert.Empty(started.Errors);
            Assert.Contains("proxy config", await pulled.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
        finally
        {
            shell.Invoke("Stop-ZbxActiveProxy probe -Confirm:$false");
        }
    }

    // ----- listeners: the credentials decide what is accepted ----------------------------------------------

    [Fact]
    public void A_passive_agent_given_a_key_accepts_that_key_and_refuses_plaintext()
    {
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke($"(Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Address 127.0.0.1 -Port 0 -PskIdentity {Identity} -Psk {KeyArgument} -Confirm:$false -PassThru).Listener.LocalEndPoint.Port");
            Assert.Empty(started.Errors);
            int port = (int)Assert.Single(started.Output).BaseObject;

            Run overPsk = shell.Invoke($"Get-ZbxAgentItem 127.0.0.1:{port} agent.ping -PskIdentity {Identity} -Psk {KeyArgument}");
            Run plain = shell.Invoke($"Get-ZbxAgentItem 127.0.0.1:{port} agent.ping -Timeout 5");

            Assert.Empty(overPsk.Errors);
            Assert.Equal("1", Assert.IsType<ZabbixAgentAnswer>(Assert.Single(overPsk.Output).BaseObject).Value);
            Assert.NotEmpty(plain.Errors);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public void A_passive_agent_with_TlsAccept_takes_plaintext_beside_its_key()
    {
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke($"(Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Address 127.0.0.1 -Port 0 -TlsAccept Unencrypted,Psk -PskIdentity {Identity} -Psk {KeyArgument} -Confirm:$false -PassThru).Listener.LocalEndPoint.Port");
            int port = (int)Assert.Single(started.Output).BaseObject;

            Run plain = shell.Invoke($"Get-ZbxAgentItem 127.0.0.1:{port} agent.ping");

            Assert.Empty(plain.Errors);
            Assert.Equal("1", Assert.IsType<ZabbixAgentAnswer>(Assert.Single(plain.Output).BaseObject).Value);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    // ----- certificates end to end -------------------------------------------------------------------------

    [Fact]
    public void A_passive_agent_and_the_getter_talk_over_certificates_and_the_subject_pin_is_held()
    {
        Pki pki = Pki.Create(_directory);
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke($"""
                Set-Location '{_directory}'
                (Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Address 127.0.0.1 -Port 0 -Certificate agent.crt -CertificateKey agent.key -CertificateAuthority ca.crt -Confirm:$false -PassThru).Listener.LocalEndPoint.Port
                """);
            Assert.Empty(started.Errors);
            int port = (int)Assert.Single(started.Output).BaseObject;
            string client = $"-Certificate (Get-Item client.crt) -CertificateKey ./client.key -CertificateAuthority ca.crt";

            Run pinned = shell.Invoke($"Get-ZbxAgentItem 127.0.0.1:{port} agent.ping {client} -ServerSubject '{Pki.AgentSubject}' -ServerIssuer '{Pki.CaSubject}' -Verbose");
            Run wrongPin = shell.Invoke($"Get-ZbxAgentItem 127.0.0.1:{port} agent.ping {client} -ServerSubject 'CN=someone else' -Timeout 5");
            Run plain = shell.Invoke($"Get-ZbxAgentItem 127.0.0.1:{port} agent.ping -Timeout 5");

            Assert.True(pinned.Errors.Count == 0, string.Join("\n", pinned.Verbose.Concat(pinned.Warnings).Concat(pinned.Errors.Select(e => e.ToString()))));
            Assert.Equal("1", Assert.IsType<ZabbixAgentAnswer>(Assert.Single(pinned.Output).BaseObject).Value);
            Assert.NotEmpty(wrongPin.Errors);
            Assert.NotEmpty(plain.Errors);
            Assert.True(File.Exists(pki.ClientKey));
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public void Connect_ZbxServer_reads_the_certificate_files_when_it_is_given_them()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Connect-ZbxServer -Uri http://127.0.0.1:1/ -TrapperCertificate '{_directory}/none.crt' -TrapperCertificateKey '{_directory}/none.key' -TrapperCertificateAuthority '{_directory}/none-ca.crt'");

        Assert.NotEmpty(run.Errors);
        Assert.Empty(shell.Context.Servers);
    }

    /// <summary>A test CA, an agent certificate (server authentication) and a client certificate, as PEM files.</summary>
    private sealed record Pki(string ClientKey)
    {
        public const string CaSubject = "CN=zbx-test-ca,O=Zabbix tests";
        public const string AgentSubject = "CN=zbx-test-agent,O=Zabbix tests";

        public static Pki Create(string directory)
        {
            using ECDsa caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var caRequest = new CertificateRequest("CN=zbx-test-ca, O=Zabbix tests", caKey, HashAlgorithmName.SHA256);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
            using X509Certificate2 ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            File.WriteAllText(Path.Combine(directory, "ca.crt"), ca.ExportCertificatePem());
            Issue(directory, ca, "agent", "CN=zbx-test-agent, O=Zabbix tests", "1.3.6.1.5.5.7.3.1", 1);
            Issue(directory, ca, "client", "CN=zbx-test-client, O=Zabbix tests", "1.3.6.1.5.5.7.3.2", 2);
            return new Pki(Path.Combine(directory, "client.key"));
        }

        private static void Issue(string directory, X509Certificate2 ca, string name, string subject, string usage, byte serial)
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
            // Inside the CA's validity, read from the CA itself: "now + 30 days" computed again here can land a tick past it.
            using X509Certificate2 leaf = request.Create(ca, ca.NotBefore, ca.NotAfter, [serial, 7, 7]);
            File.WriteAllText(Path.Combine(directory, name + ".crt"), leaf.ExportCertificatePem());
            File.WriteAllText(Path.Combine(directory, name + ".key"), key.ExportPkcs8PrivateKeyPem());
        }
    }
}
