using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HttpSpy.Core.Proxy;

/// <summary>
/// Manages the HttpSpy root certificate authority and issues short-lived leaf
/// certificates for each intercepted host so that HTTPS traffic can be
/// decrypted (man-in-the-middle). The root CA is persisted to disk so it only
/// has to be trusted by the OS once.
/// </summary>
public sealed class CertificateAuthority : IDisposable
{
    private const string RootSubject = "CN=HttpSpy Root CA, O=HttpSpy, OU=HTTP Debugger";

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, X509Certificate2> _leafCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _storeDirectory;

    public CertificateAuthority(string? storeDirectory = null)
    {
        _storeDirectory = storeDirectory ?? DefaultStoreDirectory();
        Directory.CreateDirectory(_storeDirectory);
        RootCertificate = LoadOrCreateRoot();
    }

    public X509Certificate2 RootCertificate { get; private set; }

    public string RootCertificatePath => Path.Combine(_storeDirectory, "HttpSpyRootCA.cer");
    public string RootPfxPath => Path.Combine(_storeDirectory, "HttpSpyRootCA.pfx");

    public static string DefaultStoreDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(baseDir, "HttpSpy", "Certificates");
    }

    /// <summary>Returns (creating if necessary) a leaf certificate valid for the host.</summary>
    public X509Certificate2 GetCertificateForHost(string host)
    {
        host = NormalizeHost(host);
        if (_leafCache.TryGetValue(host, out var cached) && cached.NotAfter > DateTime.Now.AddMinutes(1))
            return cached;

        lock (_gate)
        {
            if (_leafCache.TryGetValue(host, out cached) && cached.NotAfter > DateTime.Now.AddMinutes(1))
                return cached;

            var leaf = CreateLeafCertificate(host);
            _leafCache[host] = leaf;
            return leaf;
        }
    }

    private static string NormalizeHost(string host)
    {
        int colon = host.IndexOf(':');
        if (colon >= 0) host = host[..colon];
        return host.Trim('.').ToLowerInvariant();
    }

    private X509Certificate2 LoadOrCreateRoot()
    {
        if (File.Exists(RootPfxPath))
        {
            try
            {
                var existing = new X509Certificate2(RootPfxPath, "httpspy",
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
                if (existing.NotAfter > DateTime.Now.AddDays(7))
                    return existing;
            }
            catch
            {
                // fall through and regenerate
            }
        }

        return CreateRoot();
    }

    private X509Certificate2 CreateRoot()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(RootSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(10);
        var cert = request.CreateSelfSigned(notBefore, notAfter);

        // Persist a PFX (with key) for ourselves and a DER .cer for the user to trust.
        var pfx = cert.Export(X509ContentType.Pfx, "httpspy");
        File.WriteAllBytes(RootPfxPath, pfx);
        File.WriteAllBytes(RootCertificatePath, cert.Export(X509ContentType.Cert));

        return new X509Certificate2(pfx, "httpspy",
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    private X509Certificate2 CreateLeafCertificate(string host)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth

        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var ip))
            san.AddIpAddress(ip);
        else
        {
            san.AddDnsName(host);
            if (!host.StartsWith("*.") && host.Count(c => c == '.') >= 1)
                san.AddDnsName("*." + host);
        }
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(1);

        using var issued = request.Create(RootCertificate, notBefore, notAfter, serial);
        var withKey = issued.CopyWithPrivateKey(rsa);

        // Re-import through a PFX round-trip so the key is usable by SslStream on
        // every platform (avoids ephemeral-key issues on Windows).
        var exported = withKey.Export(X509ContentType.Pfx);
        withKey.Dispose();
        return new X509Certificate2(exported, (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    /// <summary>Exports the public root certificate (DER) to the supplied path.</summary>
    public void ExportRootCertificate(string path) =>
        File.WriteAllBytes(path, RootCertificate.Export(X509ContentType.Cert));

    public void Dispose()
    {
        foreach (var c in _leafCache.Values) c.Dispose();
        _leafCache.Clear();
        RootCertificate.Dispose();
    }
}
