using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace HttpSpy.Core.Proxy;

/// <summary>Installs / removes the HttpSpy root certificate from the OS trust store (Windows).</summary>
public static class CertTrust
{
    /// <summary>Adds the root CA to the current user's Trusted Root store. Windows only.</summary>
    [SupportedOSPlatform("windows")]
    public static void Install(X509Certificate2 rootCertificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        // Store only the public certificate, never the private key.
        using var publicOnly = new X509Certificate2(rootCertificate.Export(X509ContentType.Cert));
        store.Add(publicOnly);
        store.Close();
    }

    [SupportedOSPlatform("windows")]
    public static bool IsInstalled(X509Certificate2 rootCertificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, rootCertificate.Thumbprint, false);
        store.Close();
        return found.Count > 0;
    }

    [SupportedOSPlatform("windows")]
    public static void Uninstall(X509Certificate2 rootCertificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, rootCertificate.Thumbprint, false);
        foreach (var c in found) store.Remove(c);
        store.Close();
    }
}
