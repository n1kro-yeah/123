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

    /// <summary>
    /// Upper bound on cached leaf certificates. A browsing session touches
    /// hundreds of hosts; without a bound the cache (and the RSA keys it pins)
    /// grows for as long as the app runs.
    /// </summary>
    public int MaxCachedLeaves { get; set; } = 512;

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
            _issueOrder.Enqueue(host);
            TrimCache();
            return leaf;
        }
    }

    private readonly Queue<string> _issueOrder = new();

    /// <summary>Evicts the oldest leaves once the cache exceeds its bound. Caller holds <c>_gate</c>.</summary>
    private void TrimCache()
    {
        while (_issueOrder.Count > MaxCachedLeaves)
        {
            var oldest = _issueOrder.Dequeue();
            // Skip entries that were re-issued in the meantime; their newer key
            // is still live and will be evicted by its own queue entry.
            if (_issueOrder.Contains(oldest)) continue;
            if (_leafCache.TryRemove(oldest, out var evicted))
            {
                try { evicted.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Normalises an authority to the DNS name a leaf should be issued for:
    /// strips a trailing port, trailing dots and case. IPv6 literals keep their
    /// colons (splitting on the first colon would mangle them).
    /// </summary>
    private static string NormalizeHost(string host)
    {
        host = host.Trim();
        if (host.StartsWith('['))
        {
            int close = host.IndexOf(']');
            if (close > 0) return host[1..close].ToLowerInvariant();
        }
        else
        {
            // A single colon means host:port; several mean a bare IPv6 literal.
            int colon = host.IndexOf(':');
            if (colon >= 0 && colon == host.LastIndexOf(':')) host = host[..colon];
        }
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
        RestrictToOwner(RootPfxPath);
        File.WriteAllBytes(RootCertificatePath, cert.Export(X509ContentType.Cert));

        return new X509Certificate2(pfx, "httpspy",
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    /// <summary>
    /// Tightens the file mode of the CA private key to owner-only on Unix. The
    /// key can mint a certificate for any host, so it should not be world
    /// readable in a shared home directory.
    /// </summary>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return; // ACLs already inherit from the user profile
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // Best effort — a filesystem that cannot express this (e.g. a mounted
            // share) should not stop the CA from being created.
        }
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
        // Chain building in some clients wants the issuer link spelled out.
        request.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(RootCertificate,
                includeKeyIdentifier: true, includeIssuerAndSerial: false));

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F; // keep the DER INTEGER positive

        // Some clients (notably Chrome/Safari) reject server certificates whose
        // validity exceeds 398 days, so stay inside that window.
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(397);

        using var issued = request.Create(RootCertificate, notBefore, notAfter, serial);
        using var withKey = issued.CopyWithPrivateKey(rsa);

        // Re-import through a PFX round-trip so the key is usable by SslStream on
        // every platform (avoids ephemeral-key issues on Windows). Deliberately
        // *without* PersistKeySet: persisting would write a CNG key container to
        // disk for every host visited and never clean it up.
        var exported = withKey.Export(X509ContentType.Pfx);
        return new X509Certificate2(exported, (string?)null, X509KeyStorageFlags.Exportable);
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
