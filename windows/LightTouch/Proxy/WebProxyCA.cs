// The web proxy's per-device certificate authority, as DeviceRuntime's WebProxyCA.swift makes it: the guest trusts
// its public certificate, and the proxy signs a week-long leaf per HTTPS host with it. RSA-2048 keys and SHA-1
// signatures (iOS 3's TLS 1.0 stack), valid from 2000 (a device whose clock is pinned years back still accepts them).
//
// Files beside the routing CONFIG (web-proxy.conf), in the Mac's layout so a CA moves between them: CONFIG.ca.pem
// (the PKCS#8 private key, then the certificate; under %LOCALAPPDATA%, the user's own) and CONFIG.ca.der.

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LightTouch.Proxy;

internal sealed class WebProxyCA
{
    public RSA Key { get; }
    public X509Certificate2 Certificate { get; }

    private WebProxyCA(RSA key, X509Certificate2 certificate)
    {
        Key = key;
        Certificate = certificate;
    }

    private static readonly DateTimeOffset ValidFrom = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly object CreationLock = new();

    /// Loads CONFIG.ca.pem, creating it on first use; (re)writes CONFIG.ca.der.
    public static WebProxyCA Prepare(string config)
    {
        lock (CreationLock)
        {
            var pem = config + ".ca.pem";
            if (!File.Exists(pem))
            {
                using var key = RSA.Create(2048);
                var name = Name("Light Touch Device Proxy");
                var request = new CertificateRequest(name, key, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
                using var certificate = request.Create(name, new Sha1Signer(key),
                    ValidFrom, DateTimeOffset.UtcNow.AddDays(3650), Serial());
                var temporary = pem + $".{Environment.ProcessId}.tmp";
                File.WriteAllText(temporary, key.ExportPkcs8PrivateKeyPem() + "\n" + certificate.ExportCertificatePem() + "\n");
                File.Move(temporary, pem, overwrite: true);
            }
            var ca = Load(config);
            File.WriteAllBytes(config + ".ca.der", ca.Certificate.RawData);
            return ca;
        }
    }

    public static WebProxyCA Load(string config)
    {
        var text = File.ReadAllText(config + ".ca.pem");
        var key = RSA.Create();
        key.ImportFromPem(text);
        var certificate = X509Certificate2.CreateFromPem(text);
        using var certificateKey = certificate.GetRSAPublicKey();
        if (certificateKey == null || !certificateKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
        {
            throw new CryptographicException($"{config}.ca.pem's key isn't its certificate's.");
        }
        return new WebProxyCA(key, certificate);
    }

    /// A TLS server certificate for `host` (a DNS name or an IP literal), valid for a week, under this CA, with
    /// `leafKey`; as PEM for guesttls.dll.
    public (string Certificate, string Key) Leaf(string host, RSA leafKey)
    {
        if (host.Length is 0 or > 253 || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':'))
        {
            throw new CryptographicException($"Not a host name: {host}");
        }
        var request = new CertificateRequest(Name(host.Length <= 64 ? host : "Light Touch Device Proxy"), leafKey,
            HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var names = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var address)) names.AddIpAddress(address);
        else names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.Create(Certificate.SubjectName, new Sha1Signer(Key),
            ValidFrom, DateTimeOffset.UtcNow.AddDays(7), Serial());
        return (certificate.ExportCertificatePem(), leafKey.ExportPkcs8PrivateKeyPem());
    }

    private static X500DistinguishedName Name(string commonName)
    {
        var builder = new X500DistinguishedNameBuilder();
        builder.AddCommonName(commonName);
        return builder.Build();
    }

    private static byte[] Serial()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f;
        return serial;
    }
}

/// sha1WithRSAEncryption, which .NET's own RSA signature generator no longer makes.
internal sealed class Sha1Signer(RSA key) : X509SignatureGenerator
{
    private static readonly byte[] Sha1WithRsa = [0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x05, 0x05, 0x00];

    public override byte[] GetSignatureAlgorithmIdentifier(HashAlgorithmName hashAlgorithm) => Sha1WithRsa;

    public override byte[] SignData(byte[] data, HashAlgorithmName hashAlgorithm) =>
        key.SignData(data, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);

    protected override PublicKey BuildPublicKey() => new(key);
}
