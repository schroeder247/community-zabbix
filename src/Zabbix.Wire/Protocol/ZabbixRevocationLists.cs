// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Community.Zabbix.Protocol;

/// <summary>
/// Represents Zabbix's <c>TLSCRLFile</c>: the certificate revocation lists a certificate-mode connection checks the
/// peer's chain against, the way upstream sets its store (<c>X509_V_FLAG_CRL_CHECK | X509_V_FLAG_CRL_CHECK_ALL</c>,
/// <c>src/libs/zbxcomms/tls_openssl.c</c>): every certificate of the chain, the trust anchor included, must be covered
/// by a list its issuer signed that is current (<c>thisUpdate</c> passed, <c>nextUpdate</c> not), and must not be on it.
/// A list whose signature this check cannot verify covers nothing, so it fails closed.
/// </summary>
/// <remarks>
/// Owned because .NET cannot be handed a CRL: <see cref="X509ChainPolicy.ExtraStore"/> and
/// <see cref="X509ChainPolicy.CustomTrustStore"/> hold certificates only; <see cref="X509RevocationMode.Offline"/> reads
/// only the process-wide CRL cache, and seeding it would change every other chain in the process; and
/// <see cref="CertificateRevocationListBuilder.Load(byte[], out System.Numerics.BigInteger)"/> does not verify a list's
/// signature and exposes no issuer, dates or entries. So the list is read with <see cref="AsnReader"/> (RFC 5280 §5.1)
/// and its signature verified with the issuer's public key.
/// </remarks>
internal sealed class ZabbixRevocationLists
{
    private readonly List<RevocationList> _lists;

    private ZabbixRevocationLists(List<RevocationList> lists) => _lists = lists;

    /// <summary>Reads every <c>X509 CRL</c> PEM block of <paramref name="path"/>.</summary>
    /// <param name="path">The file.</param>
    /// <exception cref="InvalidDataException">The file holds no list, or a list is not RFC 5280.</exception>
    public static ZabbixRevocationLists Load(string path) => Parse(File.ReadAllText(path), path);

    internal static ZabbixRevocationLists Parse(ReadOnlySpan<char> pem, string origin)
    {
        var lists = new List<RevocationList>();
        while (PemEncoding.TryFind(pem, out PemFields fields))
        {
            if (pem[fields.Label].SequenceEqual("X509 CRL"))
            {
                try { lists.Add(RevocationList.Read(Convert.FromBase64String(new string(pem[fields.Base64Data])))); }
                catch (AsnContentException invalid) { throw new InvalidDataException($"A CRL in {origin} is not RFC 5280: {invalid.Message}", invalid); }
            }
            pem = pem[fields.Location.End..];
        }
        return lists.Count > 0 ? new ZabbixRevocationLists(lists) : throw new InvalidDataException($"The file {origin} holds no X509 CRL block.");
    }

    /// <summary>Whether no certificate of <paramref name="chain"/> is revoked, by lists that cover every one of them.</summary>
    /// <param name="chain">The built chain, leaf first.</param>
    /// <param name="now">The time the lists must be current at.</param>
    /// <param name="reason">Why not, when not.</param>
    public bool Check(X509Chain chain, DateTimeOffset now, out string? reason)
    {
        X509ChainElementCollection elements = chain.ChainElements;
        for (int i = 0; i < elements.Count; i++)
        {
            X509Certificate2 certificate = elements[i].Certificate;
            X509Certificate2 issuer = i + 1 < elements.Count ? elements[i + 1].Certificate : certificate;
            RevocationList? list = _lists.FirstOrDefault(candidate => candidate.IsFrom(issuer));
            if (list is null) { reason = $"no CRL signed by {issuer.Subject} covers {certificate.Subject}"; return false; }
            if (now < list.ThisUpdate) { reason = $"the CRL of {issuer.Subject} is not valid until {list.ThisUpdate:O}"; return false; }
            if (list.NextUpdate is { } next && now > next) { reason = $"the CRL of {issuer.Subject} expired at {next:O}"; return false; }
            if (list.Revoked.Contains(Convert.ToHexString(certificate.SerialNumberBytes.Span)))
            {
                reason = $"{certificate.Subject} (serial {certificate.SerialNumber}) is revoked by {issuer.Subject}";
                return false;
            }
        }
        reason = null;
        return true;
    }

    private sealed class RevocationList
    {
        private ReadOnlyMemory<byte> Issuer { get; init; }

        private ReadOnlyMemory<byte> Signed { get; init; }

        private string Algorithm { get; init; } = string.Empty;

        private byte[] Signature { get; init; } = [];

        public DateTimeOffset ThisUpdate { get; private init; }

        public DateTimeOffset? NextUpdate { get; private init; }

        public HashSet<string> Revoked { get; } = new(StringComparer.Ordinal);

        // CertificateList ::= SEQUENCE { tbsCertList, signatureAlgorithm, signatureValue } (RFC 5280 §5.1).
        public static RevocationList Read(byte[] der)
        {
            AsnReader certificateList = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
            ReadOnlyMemory<byte> signed = certificateList.PeekEncodedValue();
            AsnReader tbs = certificateList.ReadSequence();
            string algorithm = certificateList.ReadSequence().ReadObjectIdentifier();
            byte[] signature = certificateList.ReadBitString(out _);

            if (tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Integer)) tbs.ReadInteger();   // version, v2
            tbs.ReadSequence();                                                             // the signature algorithm again
            ReadOnlyMemory<byte> issuer = tbs.ReadEncodedValue();
            DateTimeOffset thisUpdate = ReadTime(tbs);
            DateTimeOffset? nextUpdate = tbs.HasData && IsTime(tbs.PeekTag()) ? ReadTime(tbs) : null;
            var list = new RevocationList { Issuer = issuer, Signed = signed, Algorithm = algorithm, Signature = signature, ThisUpdate = thisUpdate, NextUpdate = nextUpdate };
            if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
            {
                AsnReader revoked = tbs.ReadSequence();
                while (revoked.HasData)
                    list.Revoked.Add(Convert.ToHexString(revoked.ReadSequence().ReadIntegerBytes().Span));
            }
            return list;
        }

        public bool IsFrom(X509Certificate2 issuer) => issuer.SubjectName.RawData.AsSpan().SequenceEqual(Issuer.Span) && Verify(issuer);

        private bool Verify(X509Certificate2 issuer)
        {
            (HashAlgorithmName hash, bool ecdsa)? scheme = Algorithm switch
            {
                "1.2.840.113549.1.1.5" => (HashAlgorithmName.SHA1, false),
                "1.2.840.113549.1.1.11" => (HashAlgorithmName.SHA256, false),
                "1.2.840.113549.1.1.12" => (HashAlgorithmName.SHA384, false),
                "1.2.840.113549.1.1.13" => (HashAlgorithmName.SHA512, false),
                "1.2.840.10045.4.3.2" => (HashAlgorithmName.SHA256, true),
                "1.2.840.10045.4.3.3" => (HashAlgorithmName.SHA384, true),
                "1.2.840.10045.4.3.4" => (HashAlgorithmName.SHA512, true),
                _ => null,                                                                  // RSASSA-PSS, EdDSA: not verified, so not trusted
            };
            if (scheme is not { } known) return false;
            if (known.ecdsa)
            {
                using ECDsa? ec = issuer.GetECDsaPublicKey();
                return ec is not null && ec.VerifyData(Signed.Span, Signature, known.hash, DSASignatureFormat.Rfc3279DerSequence);
            }
            using RSA? rsa = issuer.GetRSAPublicKey();
            return rsa is not null && rsa.VerifyData(Signed.Span, Signature, known.hash, RSASignaturePadding.Pkcs1);
        }

        private static bool IsTime(Asn1Tag tag) => tag.HasSameClassAndValue(Asn1Tag.UtcTime) || tag.HasSameClassAndValue(Asn1Tag.GeneralizedTime);

        private static DateTimeOffset ReadTime(AsnReader reader)
            => reader.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime) ? reader.ReadUtcTime() : reader.ReadGeneralizedTime();
    }
}
