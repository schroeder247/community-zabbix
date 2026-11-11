// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents the item under test in an <c>item.test</c> request: what the server reads to collect a value, or the
/// value to preprocess, and the preprocessing steps. Zabbix's frontend sends only the fields the item's type needs.
/// </summary>
/// <remarks>
/// Zabbix.Wire doesn't reference the API's records, so an enumeration the API types (the item's type, its value type)
/// is its number here: <c>(int)ZabbixItemType.ZabbixAgent</c>. Fields this record doesn't model, such as an 8.0
/// telemetry item's, go in <see cref="AdditionalFields"/>.
/// </remarks>
public sealed record ZabbixItemTestItem : IJsonOnSerializing
{
    /// <summary>The item's type, the API's <c>ZabbixItemType</c> as its number.</summary>
    [JsonPropertyName("type")]
    public int? ItemType { get; set; }

    /// <summary>The item's key.</summary>
    [JsonPropertyName("key")]
    public ZabbixItemKey? Key { get; set; }

    /// <summary>
    /// The type of the item's value, the API's <c>ZabbixItemValueType</c> as its number; the preprocessing test needs
    /// it.
    /// </summary>
    [JsonPropertyName("value_type")]
    public int? ValueType { get; set; }

    /// <summary>A value to preprocess instead of one the server collects.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>How long the server waits for the value.</summary>
    [JsonPropertyName("timeout")]
    public ZabbixDuration? Timeout { get; set; }

    /// <summary>
    /// The item's own text: a database monitor's SQL, an SSH or Telnet item's commands, a calculated item's formula, a
    /// script.
    /// </summary>
    [JsonPropertyName("params")]
    public string? Parameters { get; set; }

    /// <summary>A script or browser item's parameters, by name.</summary>
    [JsonPropertyName("parameters")]
    public IReadOnlyDictionary<string, string>? ScriptParameters { get; set; }

    /// <summary>The preprocessing steps, in order.</summary>
    [JsonPropertyName("steps")]
    public IReadOnlyList<ZabbixItemTestStep>? Steps { get; set; }

    /// <summary>An SNMP item's OID.</summary>
    [JsonPropertyName("snmp_oid")]
    public string? SnmpOid { get; set; }

    /// <summary>An IPMI item's sensor.</summary>
    [JsonPropertyName("ipmi_sensor")]
    public string? IpmiSensor { get; set; }

    /// <summary>A JMX item's endpoint.</summary>
    [JsonPropertyName("jmx_endpoint")]
    public string? JmxEndpoint { get; set; }

    /// <summary>How an SSH or an HTTP agent item authenticates, as the API's number for it.</summary>
    [JsonPropertyName("authtype")]
    public int? AuthenticationType { get; set; }

    /// <summary>The user name the item authenticates with.</summary>
    [JsonPropertyName("username")]
    public string? UserName { get; set; }

    /// <summary>The password the item authenticates with.</summary>
    [JsonPropertyName("password")]
    [ZabbixSecret]
    public string? Password { get; set; }

    /// <summary>An SSH item's public key file.</summary>
    [JsonPropertyName("publickey")]
    public string? PublicKeyFile { get; set; }

    /// <summary>An SSH item's private key file.</summary>
    [JsonPropertyName("privatekey")]
    public string? PrivateKeyFile { get; set; }

    /// <summary>An HTTP agent item's URL.</summary>
    [JsonPropertyName("url")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "The server reads the URL as text: it may hold a macro, which isn't a URI.")]
    public string? Url { get; set; }

    /// <summary>
    /// An HTTP agent item's query fields, as Zabbix spells them: a JSON list of one-field objects,
    /// <c>[{"name":"value"}]</c>.
    /// </summary>
    [JsonPropertyName("query_fields")]
    public string? QueryFields { get; set; }

    /// <summary>An HTTP agent item's headers, as Zabbix spells them: <c>Name: value</c> lines.</summary>
    [JsonPropertyName("headers")]
    public string? Headers { get; set; }

    /// <summary>An HTTP agent item's request body.</summary>
    [JsonPropertyName("posts")]
    public string? Posts { get; set; }

    /// <summary>How an HTTP agent item's body is written: raw, JSON or XML, as the API's number for it.</summary>
    [JsonPropertyName("post_type")]
    public int? PostType { get; set; }

    /// <summary>An HTTP agent item's method, as the API's number for it.</summary>
    [JsonPropertyName("request_method")]
    public int? RequestMethod { get; set; }

    /// <summary>
    /// What of an HTTP agent item's response is the value: the body, the headers or both, as the API's number for it.
    /// </summary>
    [JsonPropertyName("retrieve_mode")]
    public int? RetrieveMode { get; set; }

    /// <summary>Whether an HTTP agent item converts its response to JSON, as the API's number for it.</summary>
    [JsonPropertyName("output_format")]
    public int? OutputFormat { get; set; }

    /// <summary>
    /// The status codes an HTTP agent item accepts, as Zabbix spells them; an empty text accepts any.
    /// </summary>
    [JsonPropertyName("status_codes")]
    public string? StatusCodes { get; set; }

    /// <summary>Whether an HTTP agent item follows redirects.</summary>
    [JsonPropertyName("follow_redirects")]
    [JsonConverter(typeof(ZabbixFlagConverter))]
    public bool? FollowRedirects { get; set; }

    /// <summary>Whether an HTTP agent item verifies the server's certificate.</summary>
    [JsonPropertyName("verify_peer")]
    [JsonConverter(typeof(ZabbixFlagConverter))]
    public bool? VerifyPeer { get; set; }

    /// <summary>Whether an HTTP agent item verifies the server's host name.</summary>
    [JsonPropertyName("verify_host")]
    [JsonConverter(typeof(ZabbixFlagConverter))]
    public bool? VerifyHost { get; set; }

    /// <summary>The proxy an HTTP agent item connects through.</summary>
    [JsonPropertyName("http_proxy")]
    public string? HttpProxy { get; set; }

    /// <summary>An HTTP agent item's client certificate file.</summary>
    [JsonPropertyName("ssl_cert_file")]
    public string? SslCertificateFile { get; set; }

    /// <summary>An HTTP agent item's private key file.</summary>
    [JsonPropertyName("ssl_key_file")]
    public string? SslKeyFile { get; set; }

    /// <summary>The password of an HTTP agent item's private key file.</summary>
    [JsonPropertyName("ssl_key_password")]
    [ZabbixSecret]
    public string? SslKeyPassword { get; set; }

    /// <summary>The item's flags, as the API's number for them: whether low-level discovery created it.</summary>
    [JsonPropertyName("flags")]
    public int? Flags { get; set; }

    /// <summary>
    /// Fields this record doesn't model, sent as they are beside the modelled ones: what a newer server reads, by its wire
    /// name.
    /// </summary>
    [JsonExtensionData]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "System.Text.Json fills extension data through a setter, as the generated requests do.")]
    public IDictionary<string, JsonElement>? AdditionalFields { get; set; }

    private static readonly System.Collections.Frozen.FrozenSet<string> s_modelledWires = System.Collections.Frozen.FrozenSet.Create(StringComparer.Ordinal, "type", "key", "value_type", "value", "timeout", "params", "parameters", "steps", "snmp_oid", "ipmi_sensor", "jmx_endpoint", "authtype", "username", "password", "publickey", "privatekey", "url", "query_fields", "headers", "posts", "post_type", "request_method", "retrieve_mode", "output_format", "status_codes", "follow_redirects", "verify_peer", "verify_host", "http_proxy", "ssl_cert_file", "ssl_key_file", "ssl_key_password", "flags");

    void IJsonOnSerializing.OnSerializing() => ZabbixExtensionDataGuard.Refuse(AdditionalFields, nameof(ZabbixItemTestItem), s_modelledWires);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(ItemType), ItemType);
        ZabbixRecordText.Member(builder, ref more, nameof(Key), Key);
        ZabbixRecordText.Member(builder, ref more, nameof(ValueType), ValueType);
        ZabbixRecordText.Member(builder, ref more, nameof(Value), Value);
        ZabbixRecordText.Member(builder, ref more, nameof(Timeout), Timeout);
        ZabbixRecordText.Member(builder, ref more, nameof(Parameters), Parameters);
        ZabbixRecordText.Count(builder, ref more, nameof(ScriptParameters), ScriptParameters);
        ZabbixRecordText.Count(builder, ref more, nameof(Steps), Steps);
        ZabbixRecordText.Member(builder, ref more, nameof(SnmpOid), SnmpOid);
        ZabbixRecordText.Member(builder, ref more, nameof(IpmiSensor), IpmiSensor);
        ZabbixRecordText.Member(builder, ref more, nameof(JmxEndpoint), JmxEndpoint);
        ZabbixRecordText.Member(builder, ref more, nameof(AuthenticationType), AuthenticationType);
        ZabbixRecordText.Member(builder, ref more, nameof(UserName), UserName);
        ZabbixRecordText.Secret(builder, ref more, nameof(Password), Password is not null);
        ZabbixRecordText.Member(builder, ref more, nameof(PublicKeyFile), PublicKeyFile);
        ZabbixRecordText.Member(builder, ref more, nameof(PrivateKeyFile), PrivateKeyFile);
        ZabbixRecordText.Member(builder, ref more, nameof(Url), Url);
        ZabbixRecordText.Member(builder, ref more, nameof(QueryFields), QueryFields);
        ZabbixRecordText.Member(builder, ref more, nameof(Headers), Headers);
        ZabbixRecordText.Member(builder, ref more, nameof(Posts), Posts);
        ZabbixRecordText.Member(builder, ref more, nameof(PostType), PostType);
        ZabbixRecordText.Member(builder, ref more, nameof(RequestMethod), RequestMethod);
        ZabbixRecordText.Member(builder, ref more, nameof(RetrieveMode), RetrieveMode);
        ZabbixRecordText.Member(builder, ref more, nameof(OutputFormat), OutputFormat);
        ZabbixRecordText.Member(builder, ref more, nameof(StatusCodes), StatusCodes);
        ZabbixRecordText.Member(builder, ref more, nameof(FollowRedirects), FollowRedirects);
        ZabbixRecordText.Member(builder, ref more, nameof(VerifyPeer), VerifyPeer);
        ZabbixRecordText.Member(builder, ref more, nameof(VerifyHost), VerifyHost);
        ZabbixRecordText.Member(builder, ref more, nameof(HttpProxy), HttpProxy);
        ZabbixRecordText.Member(builder, ref more, nameof(SslCertificateFile), SslCertificateFile);
        ZabbixRecordText.Member(builder, ref more, nameof(SslKeyFile), SslKeyFile);
        ZabbixRecordText.Secret(builder, ref more, nameof(SslKeyPassword), SslKeyPassword is not null);
        ZabbixRecordText.Member(builder, ref more, nameof(Flags), Flags);
        ZabbixRecordText.Count(builder, ref more, nameof(AdditionalFields), AdditionalFields);
        return more;
    }

    /// <summary>Whether <paramref name="other"/> holds the same values, its lists and fields compared by value.</summary>
    /// <param name="other">The other record.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixItemTestItem? other)
        => other is not null
            && Equals(ItemType, other.ItemType)
            && Equals(Key, other.Key)
            && Equals(ValueType, other.ValueType)
            && string.Equals(Value, other.Value, StringComparison.Ordinal)
            && Equals(Timeout, other.Timeout)
            && string.Equals(Parameters, other.Parameters, StringComparison.Ordinal)
            && ZabbixValueEquality.DictionaryEqual(ScriptParameters, other.ScriptParameters)
            && ZabbixValueEquality.ListEqual(Steps, other.Steps)
            && string.Equals(SnmpOid, other.SnmpOid, StringComparison.Ordinal)
            && string.Equals(IpmiSensor, other.IpmiSensor, StringComparison.Ordinal)
            && string.Equals(JmxEndpoint, other.JmxEndpoint, StringComparison.Ordinal)
            && Equals(AuthenticationType, other.AuthenticationType)
            && string.Equals(UserName, other.UserName, StringComparison.Ordinal)
            && string.Equals(Password, other.Password, StringComparison.Ordinal)
            && string.Equals(PublicKeyFile, other.PublicKeyFile, StringComparison.Ordinal)
            && string.Equals(PrivateKeyFile, other.PrivateKeyFile, StringComparison.Ordinal)
            && string.Equals(Url, other.Url, StringComparison.Ordinal)
            && string.Equals(QueryFields, other.QueryFields, StringComparison.Ordinal)
            && string.Equals(Headers, other.Headers, StringComparison.Ordinal)
            && string.Equals(Posts, other.Posts, StringComparison.Ordinal)
            && Equals(PostType, other.PostType)
            && Equals(RequestMethod, other.RequestMethod)
            && Equals(RetrieveMode, other.RetrieveMode)
            && Equals(OutputFormat, other.OutputFormat)
            && string.Equals(StatusCodes, other.StatusCodes, StringComparison.Ordinal)
            && Equals(FollowRedirects, other.FollowRedirects)
            && Equals(VerifyPeer, other.VerifyPeer)
            && Equals(VerifyHost, other.VerifyHost)
            && string.Equals(HttpProxy, other.HttpProxy, StringComparison.Ordinal)
            && string.Equals(SslCertificateFile, other.SslCertificateFile, StringComparison.Ordinal)
            && string.Equals(SslKeyFile, other.SslKeyFile, StringComparison.Ordinal)
            && string.Equals(SslKeyPassword, other.SslKeyPassword, StringComparison.Ordinal)
            && Equals(Flags, other.Flags)
            && ZabbixValueEquality.DictionaryEqual(AdditionalFields, other.AdditionalFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ItemType);
        hash.Add(Key);
        hash.Add(ValueType);
        hash.Add(Value, StringComparer.Ordinal);
        hash.Add(Timeout);
        hash.Add(Parameters, StringComparer.Ordinal);
        ZabbixValueEquality.AddDictionary(ref hash, ScriptParameters);
        ZabbixValueEquality.AddList(ref hash, Steps);
        hash.Add(SnmpOid, StringComparer.Ordinal);
        hash.Add(IpmiSensor, StringComparer.Ordinal);
        hash.Add(JmxEndpoint, StringComparer.Ordinal);
        hash.Add(AuthenticationType);
        hash.Add(UserName, StringComparer.Ordinal);
        hash.Add(Password, StringComparer.Ordinal);
        hash.Add(PublicKeyFile, StringComparer.Ordinal);
        hash.Add(PrivateKeyFile, StringComparer.Ordinal);
        hash.Add(Url, StringComparer.Ordinal);
        hash.Add(QueryFields, StringComparer.Ordinal);
        hash.Add(Headers, StringComparer.Ordinal);
        hash.Add(Posts, StringComparer.Ordinal);
        hash.Add(PostType);
        hash.Add(RequestMethod);
        hash.Add(RetrieveMode);
        hash.Add(OutputFormat);
        hash.Add(StatusCodes, StringComparer.Ordinal);
        hash.Add(FollowRedirects);
        hash.Add(VerifyPeer);
        hash.Add(VerifyHost);
        hash.Add(HttpProxy, StringComparer.Ordinal);
        hash.Add(SslCertificateFile, StringComparer.Ordinal);
        hash.Add(SslKeyFile, StringComparer.Ordinal);
        hash.Add(SslKeyPassword, StringComparer.Ordinal);
        hash.Add(Flags);
        ZabbixValueEquality.AddDictionary(ref hash, AdditionalFields);
        return hash.ToHashCode();
    }
}
