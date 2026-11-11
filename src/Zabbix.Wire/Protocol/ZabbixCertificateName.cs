// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;
using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Community.Zabbix.Protocol;

/// <summary>
/// Renders a certificate's issuer or subject as Zabbix compares it: <c>X509_NAME_print_ex(…, XN_FLAG_RFC2253 &amp;
/// ~ASN1_STRFLGS_ESC_MSB)</c> (<c>zbx_x509_dn_gets</c>, <c>src/libs/zbxcomms/tls_openssl.c</c>), the string a
/// <c>TLSServerCertIssuer</c>/<c>TLSServerCertSubject</c> pin is <c>strcmp</c>ed against.
/// </summary>
/// <remarks>
/// RDNs most specific first, <c>,</c> between them and <c>+</c> inside one (both without spaces, a multi-valued RDN
/// reversed too), OpenSSL's short names, RFC 2253 backslash escapes, UTF-8 left as is, and an unnamed attribute printed
/// as its dotted OID with the value's DER in hex. No BCL rendering is this string: <see cref="X500DistinguishedName.Name"/>
/// separates with <c>", "</c>, prints <c>S</c> and <c>E</c>, quotes instead of escaping and keeps a multi-valued RDN in
/// encoded order. Built on <see cref="X500DistinguishedName.EnumerateRelativeDistinguishedNames(bool)"/> and
/// <see cref="AsnReader"/>.
/// </remarks>
internal static class ZabbixCertificateName
{
    // OpenSSL's short names (crypto/objects/objects.txt) for the attributes a certificate name carries.
    private static readonly FrozenDictionary<string, string> s_shortNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["2.5.4.3"] = "CN",
        ["2.5.4.4"] = "SN",
        ["2.5.4.5"] = "serialNumber",
        ["2.5.4.6"] = "C",
        ["2.5.4.7"] = "L",
        ["2.5.4.8"] = "ST",
        ["2.5.4.9"] = "street",
        ["2.5.4.10"] = "O",
        ["2.5.4.11"] = "OU",
        ["2.5.4.12"] = "title",
        ["2.5.4.13"] = "description",
        ["2.5.4.15"] = "businessCategory",
        ["2.5.4.17"] = "postalCode",
        ["2.5.4.41"] = "name",
        ["2.5.4.42"] = "GN",
        ["2.5.4.43"] = "initials",
        ["2.5.4.44"] = "generationQualifier",
        ["2.5.4.46"] = "dnQualifier",
        ["2.5.4.65"] = "pseudonym",
        ["2.5.4.97"] = "organizationIdentifier",
        ["1.2.840.113549.1.9.1"] = "emailAddress",
        ["0.9.2342.19200300.100.1.1"] = "UID",
        ["0.9.2342.19200300.100.1.25"] = "DC",
        ["1.3.6.1.4.1.311.60.2.1.1"] = "jurisdictionL",
        ["1.3.6.1.4.1.311.60.2.1.2"] = "jurisdictionST",
        ["1.3.6.1.4.1.311.60.2.1.3"] = "jurisdictionC",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The name as upstream prints it for the pin compare.</summary>
    /// <param name="name">The issuer or subject.</param>
    public static string Format(X500DistinguishedName name)
    {
        var text = new StringBuilder();
        foreach (X500RelativeDistinguishedName rdn in name.EnumerateRelativeDistinguishedNames(reversed: true))
        {
            if (text.Length > 0) text.Append(',');
            List<(string Oid, ReadOnlyMemory<byte> Value)> attributes = Attributes(rdn.RawData);
            for (int i = attributes.Count - 1; i >= 0; i--)
            {
                AppendAttribute(text, attributes[i].Oid, attributes[i].Value);
                if (i > 0) text.Append('+');
            }
        }
        return text.ToString();
    }

    private static List<(string Oid, ReadOnlyMemory<byte> Value)> Attributes(ReadOnlyMemory<byte> rdn)
    {
        var attributes = new List<(string, ReadOnlyMemory<byte>)>();
        AsnReader set = new AsnReader(rdn, AsnEncodingRules.DER).ReadSetOf();
        while (set.HasData)
        {
            AsnReader attribute = set.ReadSequence();
            attributes.Add((attribute.ReadObjectIdentifier(), attribute.ReadEncodedValue()));
        }
        return attributes;
    }

    private static void AppendAttribute(StringBuilder text, string oid, ReadOnlyMemory<byte> value)
    {
        if (!s_shortNames.TryGetValue(oid, out string? shortName))
        {
            text.Append(oid).Append("=#").Append(Convert.ToHexString(value.Span));      // XN_FLAG_DUMP_UNKNOWN_FIELDS
            return;
        }
        text.Append(shortName).Append('=');
        if (TryReadString(value, out string? decoded) && decoded is not null) Escape(text, decoded);
        else text.Append('#').Append(Convert.ToHexString(value.Span));                  // ASN1_STRFLGS_DUMP_UNKNOWN
    }

    private static bool TryReadString(ReadOnlyMemory<byte> value, out string? decoded)
    {
        var reader = new AsnReader(value, AsnEncodingRules.BER);
        Asn1Tag tag = reader.PeekTag();
        decoded = null;
        if (tag.TagClass != TagClass.Universal) return false;
        var type = (UniversalTagNumber)tag.TagValue;
        if (type is not (UniversalTagNumber.UTF8String or UniversalTagNumber.PrintableString or UniversalTagNumber.IA5String
            or UniversalTagNumber.T61String or UniversalTagNumber.BMPString or UniversalTagNumber.UniversalString
            or UniversalTagNumber.VisibleString or UniversalTagNumber.NumericString)) return false;
        decoded = reader.ReadCharacterString(type);
        return true;
    }

    // ASN1_STRFLGS_ESC_2253 | ESC_CTRL (ESC_MSB off): the RFC 2253 specials, a leading '#' or space, a trailing space,
    // and control characters as \XX.
    private static void Escape(StringBuilder text, string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool special = c is ',' or '+' or '"' or '\\' or '<' or '>' or ';'
                           || (i == 0 && c is '#' or ' ')
                           || (i == value.Length - 1 && c == ' ');
            if (special) text.Append('\\').Append(c);
            else if (c < 0x20 || c == 0x7f) text.Append('\\').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            else text.Append(c);
        }
    }
}
