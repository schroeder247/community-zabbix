// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Community.Zabbix.Hosting;

namespace Community.Zabbix.Items;

/// <summary>
/// Provides Zabbix's web keys: a page with its headers, how long it takes to load, the text on it that matches a
/// regular expression (<c>web.page.get</c>, <c>web.page.perf</c>, <c>web.page.regexp</c>), and a site's TLS
/// certificate (<c>web.certificate.get</c>, Zabbix agent 2's key).
/// </summary>
/// <remarks>
/// A page is fetched as Zabbix's agent fetches it: <c>GET</c> over a connection of its own (<c>Connection: close</c>),
/// redirects not followed, the server's certificate not checked, compressed bodies decoded, and the user agent
/// <c>Zabbix &lt;version&gt;</c>. The answer is the status line and headers, then the body.
/// </remarks>
public sealed class ZabbixWebItems : ZabbixItemProvider
{

    private readonly ZabbixItemSet _set = new();
    private readonly TimeProvider _time;

    /// <summary>The web keys.</summary>
    /// <param name="time">The clock <c>web.page.perf</c> times a page on and certificates are checked against; the system clock by default.</param>
    public ZabbixWebItems(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _set.Add("web.page.get", "A web page, its headers and body", (key, token) => PageAsync(key, 3, token), "web.page.get[localhost,,80]")
            .Add("web.page.perf", "How long a web page takes to load, in seconds", PerfAsync, "web.page.perf[localhost,,80]")
            .Add("web.page.regexp", "The text on a web page that matches a regular expression", RegexpAsync, "web.page.regexp[localhost,,80,OK]")
            .Add("web.certificate.get", "A site's TLS certificate and whether it is valid, as JSON", CertificateAsync, "web.certificate.get[example.com,443]");
    }

    /// <inheritdoc/>
    public override IReadOnlyList<ZabbixItemDefinition> Items => _set.Items;

    /// <inheritdoc/>
    public override ValueTask<ZabbixAgentAnswer> GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken = default) => _set.AnswerAsync(key, cancellationToken);

    private static async ValueTask<string> PageAsync(ZabbixItemKey key, int parameters, CancellationToken cancellationToken)
    {
        ZabbixItemSet.AtMost(key, parameters);
        return await FetchAsync(Address(key), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<string> PerfAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        ZabbixItemSet.AtMost(key, 3);
        Uri address = Address(key);
        long started = _time.GetTimestamp();
        _ = await FetchAsync(address, cancellationToken).ConfigureAwait(false);
        return ZabbixItemSet.Float(_time.GetElapsedTime(started).TotalSeconds);
    }

    // web_page_regexp (common/http.c): the first line that matches, through the output template, at most length characters.
    private static async ValueTask<string> RegexpAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        ZabbixItemSet.AtMost(key, 6);
        if (key.Parameters.Count < 4) throw new ZabbixItemException("The key has the wrong number of parameters for this type.");
        string pattern = ZabbixItemSet.Parameter(key, 3);
        string lengthText = ZabbixItemSet.Parameter(key, 4);
        int length = int.MaxValue;
        if (lengthText.Length > 0 && (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 1)) throw ZabbixItemException.InvalidParameter(4);
        Regex expression = ZabbixItemSet.Expression(pattern, 3);
        string page = await FetchAsync(Address(key), cancellationToken).ConfigureAwait(false);
        foreach (string line in page.Split('\n'))
        {
            string text = line.TrimEnd('\r');
            Match match = expression.Match(text);
            if (!match.Success) continue;
            string found = ZabbixFileItems.Substitute(text, match, ZabbixItemSet.Parameter(key, 5));
            System.Globalization.StringInfo characters = new(found);
            return characters.LengthInTextElements > length ? characters.SubstringByTextElements(0, length) : found;
        }
        return string.Empty;
    }

    // get_http_page: a URL in the first parameter (its path and port then empty), or a host with a path and a port.
    private static Uri Address(ZabbixItemKey key)
    {
        string host = ZabbixItemSet.Parameter(key, 0);
        string path = ZabbixItemSet.Parameter(key, 1);
        string port = ZabbixItemSet.Parameter(key, 2);
        if (host.Length == 0) throw ZabbixItemException.InvalidParameter(0);
        if (host.Contains("://", StringComparison.Ordinal))
        {
            if (path.Length > 0 || port.Length > 0) throw new ZabbixItemException("The path and port parameters must be empty when the host parameter is a URL.");
            if (!Uri.TryCreate(host, UriKind.Absolute, out Uri? url) || url.Scheme is not ("http" or "https")) throw new ZabbixItemException("Unsupported scheme. Supported schemes are http and https.");
            return url;
        }
        ushort number = 80;
        if (port.Length > 0 && !ushort.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out number)) throw ZabbixItemException.InvalidParameter(2);
        var builder = new UriBuilder(Uri.UriSchemeHttp, host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]" : host, number, path.TrimStart('/').Insert(0, "/"));
        return builder.Uri;
    }

    private static async Task<string> FetchAsync(Uri address, CancellationToken cancellationToken)
    {
        // An agent's check opens a connection of its own, as Zabbix's agent does (Connection: close): a handler per
        // check, disposed with it, so nothing is pooled across checks or shared with the application's clients.
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            // curl_page_get sets CURLOPT_SSL_VERIFYPEER and CURLOPT_SSL_VERIFYHOST to 0: a page check reads the page,
            // whoever's certificate the server shows (web.certificate.get is the key that judges it).
#pragma warning disable CA5359
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
#pragma warning restore CA5359
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.ConnectionClose = true;
        request.Headers.TryAddWithoutValidation("User-Agent", "Zabbix " + ZabbixWire.AgentVersion);
        HttpResponseMessage response;
        try
        {
            response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException failed)
        {
            throw new ZabbixItemException($"The request failed: {failed.Message}", failed);
        }
        using (response)
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"HTTP/{response.Version.Major}.{response.Version.Minor} {(int)response.StatusCode} {response.ReasonPhrase}\r\n");
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers.Concat(response.Content.Headers))
                text.Append(header.Key).Append(": ").Append(string.Join(", ", header.Value)).Append("\r\n");
            text.Append("\r\n");
            text.Append(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return text.ToString();
        }
    }

    // web.certificate.get (src/go/plugins/web/certificate): hostname, port, address.
    private async ValueTask<string> CertificateAsync(ZabbixItemKey key, CancellationToken cancellationToken)
    {
        if (key.Parameters.Count == 0 || ZabbixItemSet.Parameter(key, 0).Length == 0) throw new ZabbixItemException("Invalid parameters: too few parameters.");
        if (key.Parameters.Count > 3) throw new ZabbixItemException("Invalid parameters: too many parameters.");
        string domain = ZabbixItemSet.Parameter(key, 0);
        if (Uri.TryCreate(domain, UriKind.Absolute, out Uri? url) && url.Scheme == Uri.UriSchemeHttps) domain = url.Host;
        string portText = ZabbixItemSet.Parameter(key, 1);
        int port = url is { IsDefaultPort: false } ? url.Port : 443;
        if (portText.Length > 0)
            port = ushort.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out ushort parsed) ? parsed : throw new ZabbixItemException("Invalid parameters: invalid port.");
        string address = ZabbixItemSet.Parameter(key, 2) is { Length: > 0 } a ? a : domain;
        if (IPAddress.TryParse(address.Trim('[', ']'), out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
            throw new ZabbixItemException($"Invalid parameters: {address} can not be ipv6.");
        X509Certificate2 leaf;
        var intermediates = new X509Certificate2Collection();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
            // Agent 2 dials with InsecureSkipVerify and judges the certificate itself (Validate): the handshake must
            // succeed for an expired or self-signed certificate too, so that the key can say which it is.
#pragma warning disable CA5359
            var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, chain, _) =>
            {
                foreach (X509ChainElement element in chain?.ChainElements ?? (IEnumerable<X509ChainElement>)[]) intermediates.Add(X509CertificateLoader.LoadCertificate(element.Certificate.RawData));
                return true;
            });
#pragma warning restore CA5359
            await using (tls.ConfigureAwait(false))
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = domain }, cancellationToken).ConfigureAwait(false);
                leaf = X509CertificateLoader.LoadCertificate((tls.RemoteCertificate ?? throw new ZabbixItemException("Cannot fetch data.")).GetRawCertData());
            }
        }
        catch (Exception failed) when (failed is SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            throw new ZabbixItemException($"Cannot fetch data: failed to dial TLS certificate: {failed.Message}", failed);
        }
        using (leaf)
        {
            string subject = Name(leaf.SubjectName), issuer = Name(leaf.IssuerName);
            (string value, string message) = Validate(leaf, intermediates, domain, subject, issuer);
            return ZabbixItemSet.Json(json =>
            {
                json.WriteStartObject();
                json.WriteStartObject("x509");
                json.WriteNumber("version", leaf.Version);
                json.WriteString("serial_number", Serial(leaf.SerialNumber));
                json.WriteString("signature_algorithm", SignatureAlgorithm(leaf.SignatureAlgorithm.Value));
                json.WriteString("issuer", issuer);
                WriteTime("not_before", leaf.NotBefore);
                WriteTime("not_after", leaf.NotAfter);
                json.WriteString("subject", subject);
                json.WriteString("public_key_algorithm", leaf.PublicKey.Oid.Value switch
                {
                    "1.2.840.113549.1.1.1" => "RSA",
                    "1.2.840.10045.2.1" => "ECDSA",
                    "1.3.101.112" => "Ed25519",
                    "1.2.840.10040.4.1" => "DSA",
                    _ => "0",
                });
                string[] names = [.. leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(extension => extension.EnumerateDnsNames())];
                if (names.Length == 0)
                {
                    json.WriteNull("alternative_names");
                }
                else
                {
                    json.WriteStartArray("alternative_names");
                    foreach (string name in names) json.WriteStringValue(name);
                    json.WriteEndArray();
                }
                json.WriteEndObject();
                json.WriteStartObject("result");
                json.WriteString("value", value);
                json.WriteString("message", message);
                json.WriteEndObject();
#pragma warning disable CA5350 // the certificate's SHA-1 fingerprint, a published identifier of it, not a security control
                json.WriteString("sha1_fingerprint", Convert.ToHexStringLower(SHA1.HashData(leaf.RawData)));
#pragma warning restore CA5350
                json.WriteString("sha256_fingerprint", Convert.ToHexStringLower(SHA256.HashData(leaf.RawData)));
                json.WriteEndObject();

                void WriteTime(string name, DateTime time)
                {
                    DateTimeOffset utc = new DateTimeOffset(time).ToUniversalTime();
                    json.WriteStartObject(name);
                    json.WriteString("value", utc.ToString("MMM dd HH:mm:ss yyyy 'GMT'", CultureInfo.InvariantCulture));
                    json.WriteNumber("timestamp", utc.ToUnixTimeSeconds());
                    json.WriteEndObject();
                }
            });
        }
    }

    // getValidationResult: valid; valid-but-self-signed when the only fault is an unknown root and the certificate signs itself; else invalid.
    private (string Value, string Message) Validate(X509Certificate2 leaf, X509Certificate2Collection intermediates, string domain, string subject, string issuer)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.ExtraStore.AddRange(intermediates);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = _time.GetUtcNow().UtcDateTime;
        bool built = chain.Build(leaf);
        X509ChainStatusFlags faults = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, status) => all | status.Status);
        bool nameMatches = leaf.MatchesHostname(domain);
        if (built && nameMatches) return ("valid", "certificate verified successfully");
        if (nameMatches && (faults & ~(X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain)) == 0 && subject == issuer)
            return ("valid-but-self-signed", "certificate verified successfully, but determined to be self signed");
        string reason = !nameMatches
            ? $"x509: certificate is not valid for {domain}"
            : string.Join("; ", chain.ChainStatus.Select(status => status.StatusInformation.Trim()));
        return ("invalid", $"failed to verify certificate: {reason}");
    }

    // Go's RDNSequence.String(): the most specific name first, short attribute names, commas without spaces.
    private static string Name(X500DistinguishedName name)
    {
        var parts = new List<string>();
        foreach (X500RelativeDistinguishedName rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.HasMultipleElements) continue;
            string type = rdn.GetSingleElementType().Value switch
            {
                "2.5.4.3" => "CN",
                "2.5.4.6" => "C",
                "2.5.4.7" => "L",
                "2.5.4.8" => "ST",
                "2.5.4.9" => "STREET",
                "2.5.4.10" => "O",
                "2.5.4.11" => "OU",
                "2.5.4.5" => "SERIALNUMBER",
                "2.5.4.17" => "POSTALCODE",
                string other => other,
                null => "0",
            };
            parts.Add($"{type}={Escape(rdn.GetSingleElementValue() ?? string.Empty)}");
        }
        return string.Join(',', parts);

        static string Escape(string value)
        {
            var escaped = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' || (i == 0 && c is ' ' or '#') || (i == value.Length - 1 && c == ' ')) escaped.Append('\\');
                escaped.Append(c);
            }
            return escaped.ToString();
        }
    }

    // Go's "%x" of the serial's big-endian bytes, leading zero bytes dropped.
    private static string Serial(string hex)
    {
        while (hex.Length > 2 && hex.StartsWith("00", StringComparison.Ordinal)) hex = hex[2..];
        return Convert.ToHexStringLower(Convert.FromHexString(hex));
    }

    // Go's x509.SignatureAlgorithm names.
    private static string SignatureAlgorithm(string? oid) => oid switch
    {
        "1.2.840.113549.1.1.5" => "SHA1-RSA",
        "1.2.840.113549.1.1.11" => "SHA256-RSA",
        "1.2.840.113549.1.1.12" => "SHA384-RSA",
        "1.2.840.113549.1.1.13" => "SHA512-RSA",
        "1.2.840.10045.4.1" => "ECDSA-SHA1",
        "1.2.840.10045.4.3.2" => "ECDSA-SHA256",
        "1.2.840.10045.4.3.3" => "ECDSA-SHA384",
        "1.2.840.10045.4.3.4" => "ECDSA-SHA512",
        "1.3.101.112" => "Ed25519",
        _ => "0",
    };
}
