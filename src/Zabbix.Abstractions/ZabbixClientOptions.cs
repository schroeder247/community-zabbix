// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel.DataAnnotations;

namespace Community.Zabbix;

/// <summary>
/// Represents the options for one Zabbix server. Bound by <c>AddZabbixClient</c> from code, from a configuration section or
/// from a hashtable, and validated before anything starts. The leaves are exactly the configuration keys. A credential
/// handed over in code (a <see cref="ZabbixCredential"/>, given to the server's builder or constructor) and
/// <see cref="ApiToken"/> are two ways to give the one credential: giving both fails validation.
/// </summary>
/// <remarks>
/// These values are read once, when the server's transport, credential and socket handler are first
/// resolved, and are not re-read on a configuration reload: a changed endpoint, token, connection cap or per-method
/// deadline takes effect in a new container (a restart). What does reload is the <c>Resilience</c> subsection: the standard
/// resilience handler reads its <c>HttpStandardResilienceOptions</c> through the options monitor and rebuilds its pipeline
/// when the bound section changes.
/// </remarks>
public sealed class ZabbixClientOptions : IValidatableObject
{
    /// <summary>
    /// Where the API is: the web frontend's address, e.g. <c>https://zabbix.example/</c>, or the JSON-RPC endpoint itself,
    /// <c>https://zabbix.example/api_jsonrpc.php</c>. An address whose path names no <c>.php</c> script is the frontend's,
    /// and the API is its <c>api_jsonrpc.php</c> (<see cref="GetApiEndpoint"/>). User info written into it
    /// (<c>https://user:password@…</c>) is dropped: <see cref="System.Net.Http.HttpClient"/> never sends it, and the SDK never
    /// keeps, logs or traces it; authenticate with <see cref="ApiToken"/> or a <c>ZabbixCredential</c>.
    /// </summary>
    [Required]
    public Uri? Endpoint { get; set; }

    /// <summary>The script every Zabbix frontend serves the API from.</summary>
    public const string ApiScript = "api_jsonrpc.php";

    /// <summary>
    /// The JSON-RPC endpoint <paramref name="address"/> names: the address itself when its path names a <c>.php</c> script,
    /// otherwise <see cref="ApiScript"/> in the folder it names. <c>https://zabbix.example/</c> and
    /// <c>https://zabbix.example/zabbix</c> become <c>…/api_jsonrpc.php</c> in that folder; a relative address is
    /// returned as it is, for the endpoint's own validation to refuse.
    /// </summary>
    /// <param name="address">The frontend's address or the endpoint.</param>
    /// <returns>The endpoint the SDK posts to.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    public static Uri GetApiEndpoint(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsAbsoluteUri || address.AbsolutePath.EndsWith(".php", StringComparison.OrdinalIgnoreCase)) return address;
        var builder = new UriBuilder(address);
        builder.Path = builder.Path.TrimEnd('/') + "/" + ApiScript;
        return builder.Uri;
    }

    /// <summary>
    /// Where the web frontend is, when it is not where the API is: the folder that holds <c>zabbix.php</c>, e.g.
    /// <c>https://zabbix.example/</c>, absolute and <c>http</c> or <c>https</c>. Unset, it is the endpoint's own folder
    /// (<c>https://zabbix.example/api_jsonrpc.php</c> gives <c>https://zabbix.example/</c>), which is right whenever the API
    /// is reached through the frontend's web server, as Zabbix ships it. Only the frontend's page links read it.
    /// </summary>
    public Uri? FrontendUri { get; set; }

    /// <summary>The API token sent as <c>Authorization: Bearer</c>; the alternative to a credential given in code, never beside one.</summary>
    [ZabbixSecret]
    public string? ApiToken { get; set; }

    /// <summary>
    /// Whether a credential may be sent to an endpoint that is plain <c>http</c> on another host; <see langword="false"/>
    /// by default. An API token or a <c>user.login</c> password sent over <c>http</c> crosses the network in clear on every
    /// call, so the server refuses to start with one unless this is set. A loopback endpoint (<c>localhost</c>,
    /// <c>127.0.0.1</c>, <c>::1</c>) never leaves the machine and is allowed, as the W3C Secure Contexts specification
    /// treats it as potentially trustworthy.
    /// </summary>
    public bool AllowUnencryptedCredentials { get; set; }

    /// <summary>The most connections held open to the server; 0 means the platform's default.</summary>
    [Range(0, int.MaxValue)]
    public int MaxConnectionsPerServer { get; set; } = 100;

    /// <summary>
    /// The largest reply body the transport will hold, in bytes; 1 GiB by default, the same bound the framed wire puts on a
    /// frame (<c>ZBX_MAX_RECV_DATA_SIZE</c>). A reply is read whole inside the request's deadline before it is parsed, so this
    /// is also the most memory one call can take; a larger reply fails the call with <see cref="InvalidDataException"/> and
    /// is not retried. The API itself has no such limit: a frontend's PHP <c>memory_limit</c> is the practical one.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxResponseContentBytes { get; set; } = 1L << 30;

    /// <summary>
    /// A deadline per JSON-RPC method, for the calls the standard handler's microservice defaults (10 s per attempt, 30 s in
    /// total) do not fit: <c>configuration.import</c> of a large template, a wide <c>history.get</c>. The value replaces
    /// both the attempt and the total timeout for that method. Keys are method names, matched without regard to case (Zabbix
    /// lower-cases the method before routing it: <c>strtolower</c> in <c>CLocalApiClient::callMethod</c>).
    /// </summary>
    public IDictionary<string, TimeSpan> Timeouts { get; } = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The validation message for a credential that would cross the network in clear.</summary>
    internal const string UnencryptedCredential = "The endpoint is plain http to another host, so a credential would cross the network in clear; use https, or set AllowUnencryptedCredentials.";

    /// <summary>Whether a credential may be sent to <paramref name="endpoint"/>: over <c>https</c>, to a loopback host, or when the options allow it.</summary>
    /// <param name="endpoint">The endpoint; <see langword="null"/> is left to the endpoint's own validation.</param>
    /// <param name="allowUnencrypted">The options' <see cref="AllowUnencryptedCredentials"/>.</param>
    /// <returns>Whether the credential may be sent.</returns>
    internal static bool AllowsCredentialsTo(Uri? endpoint, bool allowUnencrypted)
        => allowUnencrypted || endpoint is not { IsAbsoluteUri: true } || endpoint.Scheme != Uri.UriSchemeHttp || endpoint.IsLoopback;

    /// <inheritdoc/>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FrontendUri is { } frontend
            && (!frontend.IsAbsoluteUri || (frontend.Scheme != Uri.UriSchemeHttp && frontend.Scheme != Uri.UriSchemeHttps)))
        {
            yield return new ValidationResult($"FrontendUri must be an absolute http or https URI, not '{frontend}'", [nameof(FrontendUri)]);
        }
        if (ApiToken is { Length: > 0 } token && !ZabbixApiTokenCredential.IsWellFormed(token))
            yield return new ValidationResult("ApiToken holds whitespace or a control character; a token read from a file keeps its line end, trim it.", [nameof(ApiToken)]);
        if (ApiToken is { Length: > 0 } && !AllowsCredentialsTo(Endpoint, AllowUnencryptedCredentials))
            yield return new ValidationResult(UnencryptedCredential, [nameof(ApiToken), nameof(Endpoint)]);
        foreach (KeyValuePair<string, TimeSpan> timeout in Timeouts)
        {
            if (timeout.Value <= TimeSpan.Zero)
                yield return new ValidationResult($"Timeouts:{timeout.Key} must be positive", [nameof(Timeouts)]);
        }
    }
}
