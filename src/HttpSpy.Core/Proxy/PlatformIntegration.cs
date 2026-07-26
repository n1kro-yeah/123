using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace HttpSpy.Core.Proxy;

/// <summary>The desktop platform HttpSpy is running on.</summary>
public enum HostPlatform
{
    Windows,
    Linux,
    MacOS,
    Unknown
}

/// <summary>Whether the root CA is trusted, as far as we can actually tell.</summary>
public enum TrustState
{
    /// <summary>Confirmed present in a trust store we can read.</summary>
    Trusted,

    /// <summary>Confirmed absent from every trust store we can read.</summary>
    NotTrusted,

    /// <summary>No reliable way to check on this platform — do not claim either way.</summary>
    Unknown
}

/// <summary>One concrete thing the user has to do, with the command to do it.</summary>
/// <param name="Title">What the step accomplishes.</param>
/// <param name="Command">A copy-pasteable shell command, or empty for a manual step.</param>
/// <param name="Note">Why it is needed, or what to watch out for.</param>
public readonly record struct SetupStep(string Title, string Command, string Note);

/// <summary>
/// Trust-store and system-proxy integration for every platform HttpSpy runs on.
///
/// Only Windows gets fully automatic setup, because only there can a user-scoped
/// trust store be written without elevation. Everywhere else the honest answer is
/// to hand the user the exact commands for their platform — pointing at the real
/// exported file — rather than pretending the feature does not exist, which is
/// what the Windows-only code path used to do.
/// </summary>
public static class PlatformIntegration
{
    public static HostPlatform Current =>
        OperatingSystem.IsWindows() ? HostPlatform.Windows :
        OperatingSystem.IsMacOS() ? HostPlatform.MacOS :
        OperatingSystem.IsLinux() ? HostPlatform.Linux :
        HostPlatform.Unknown;

    public static string PlatformName => Current switch
    {
        HostPlatform.Windows => "Windows",
        HostPlatform.Linux => "Linux",
        HostPlatform.MacOS => "macOS",
        _ => "this platform",
    };

    /// <summary>True when the trust store can be modified from inside the app, without a shell.</summary>
    public static bool CanInstallTrustAutomatically => Current == HostPlatform.Windows;

    /// <summary>True when the OS proxy can be set from inside the app.</summary>
    public static bool CanSetSystemProxy =>
        Current == HostPlatform.Windows || (Current == HostPlatform.Linux && GSettings.IsAvailable);

    // ---- Certificate export --------------------------------------------------

    /// <summary>
    /// Writes the public root certificate in PEM form. Almost everything outside
    /// Windows — OpenSSL, curl, Node, Python, the Linux CA bundle — wants PEM, so
    /// the DER-only <c>.cer</c> export was a dead end on those platforms.
    /// </summary>
    public static void ExportPem(X509Certificate2 certificate, string path)
    {
        var der = certificate.Export(X509ContentType.Cert);
        var sb = new StringBuilder();
        sb.Append("-----BEGIN CERTIFICATE-----\n");
        sb.Append(Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks).ReplaceLineEndings("\n"));
        sb.Append("\n-----END CERTIFICATE-----\n");
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>The PEM body with no header, footer or line breaks — used to spot the cert inside a CA bundle.</summary>
    internal static string Base64Body(X509Certificate2 certificate) =>
        Convert.ToBase64String(certificate.Export(X509ContentType.Cert));

    /// <summary>The conventional file name for the exported PEM on this platform.</summary>
    public static string SuggestedPemName => "HttpSpyRootCA.pem";

    // ---- Trust state ---------------------------------------------------------

    /// <summary>
    /// Reports whether the root CA is trusted. Returns <see cref="TrustState.Unknown"/>
    /// rather than guessing when the platform gives us no readable answer.
    /// </summary>
    public static TrustState QueryTrust(X509Certificate2 root)
    {
        try
        {
            return Current switch
            {
                HostPlatform.Windows => WindowsTrust(root),
                HostPlatform.Linux => LinuxTrust(root),
                HostPlatform.MacOS => MacTrust(root),
                _ => TrustState.Unknown,
            };
        }
        catch
        {
            return TrustState.Unknown;
        }
    }

    private static TrustState WindowsTrust(X509Certificate2 root)
    {
        if (!OperatingSystem.IsWindows()) return TrustState.Unknown;
        return CertTrust.IsInstalled(root) ? TrustState.Trusted : TrustState.NotTrusted;
    }

    /// <summary>
    /// Looks for our certificate inside the distribution CA bundles. Comparing the
    /// base64 body means we do not care which of the many anchor directories the
    /// user dropped it into, only that <c>update-ca-certificates</c> picked it up.
    /// </summary>
    private static readonly string[] LinuxBundles =
    {
        "/etc/ssl/certs/ca-certificates.crt",
        "/etc/pki/tls/certs/ca-bundle.crt",
        "/etc/ssl/ca-bundle.pem",
    };

    private static TrustState LinuxTrust(X509Certificate2 root)
    {
        string body = Base64Body(root);
        // The bundle stores the same DER, wrapped at 64 characters; strip the
        // wrapping before looking for it.
        foreach (var bundle in LinuxBundles)
        {
            if (!File.Exists(bundle)) continue;
            string text;
            try { text = File.ReadAllText(bundle); }
            catch { continue; }
            var flat = new string(text.Where(c => c is not ('\n' or '\r' or ' ')).ToArray());
            if (flat.Contains(body, StringComparison.Ordinal)) return TrustState.Trusted;
        }

        return LinuxBundles.Any(File.Exists) ? TrustState.NotTrusted : TrustState.Unknown;
    }

    /// <summary>Asks the macOS keychain whether it holds a certificate with our SHA-1 hash.</summary>
    private static TrustState MacTrust(X509Certificate2 root)
    {
        var thumbprint = root.Thumbprint;
        if (string.IsNullOrEmpty(thumbprint)) return TrustState.Unknown;

        var output = RunCapture("security", "find-certificate -a -Z -c HttpSpy");
        if (output is null) return TrustState.Unknown;
        return output.Contains(thumbprint, StringComparison.OrdinalIgnoreCase)
            ? TrustState.Trusted
            : TrustState.NotTrusted;
    }

    // ---- Setup instructions --------------------------------------------------

    /// <summary>
    /// The steps that make this machine trust the HttpSpy root CA, written against
    /// the actual exported file so they can be pasted without editing.
    /// </summary>
    public static IReadOnlyList<SetupStep> TrustSteps(string pemPath) => Current switch
    {
        HostPlatform.Windows => new[]
        {
            new SetupStep("Trust the certificate",
                $"certutil -user -addstore Root \"{pemPath}\"",
                "The \"Trust cert\" button does this for you; the command is here for scripted setups."),
            new SetupStep("Firefox uses its own store",
                "",
                "Settings ▸ Privacy & Security ▸ View Certificates ▸ Authorities ▸ Import, then pick the exported file."),
        },
        HostPlatform.Linux => new[]
        {
            new SetupStep("Install into the system trust store",
                $"sudo cp \"{pemPath}\" /usr/local/share/ca-certificates/httpspy.crt && sudo update-ca-certificates",
                "Debian/Ubuntu. On Fedora/RHEL copy into /etc/pki/ca-trust/source/anchors and run sudo update-ca-trust."),
            new SetupStep("Trust it for one shell only, no root needed",
                $"export SSL_CERT_FILE=\"{pemPath}\"",
                "Covers OpenSSL-based tools including curl and Python's requests."),
            new SetupStep("Node.js",
                $"export NODE_EXTRA_CA_CERTS=\"{pemPath}\"",
                "Node ignores the system store, so it needs its own variable."),
            new SetupStep("A single curl call",
                $"curl --cacert \"{pemPath}\" https://example.com",
                "Useful for checking the setup without changing anything globally."),
            new SetupStep("Chrome / Chromium",
                $"certutil -d sql:$HOME/.pki/nssdb -A -t \"C,,\" -n HttpSpy -i \"{pemPath}\"",
                "Chromium keeps an NSS database of its own (package libnss3-tools)."),
            new SetupStep("Firefox uses its own store",
                "",
                "Settings ▸ Privacy & Security ▸ View Certificates ▸ Authorities ▸ Import, then pick the exported file."),
        },
        HostPlatform.MacOS => new[]
        {
            new SetupStep("Trust in the system keychain",
                $"sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain \"{pemPath}\"",
                "Applies to Safari, Chrome and anything using the system trust evaluation."),
            new SetupStep("Trust for your user only",
                $"security add-trusted-cert -r trustRoot -k ~/Library/Keychains/login.keychain-db \"{pemPath}\"",
                "No administrator password required."),
            new SetupStep("Node.js",
                $"export NODE_EXTRA_CA_CERTS=\"{pemPath}\"",
                "Node ignores the keychain."),
            new SetupStep("Firefox uses its own store",
                "",
                "Settings ▸ Privacy & Security ▸ View Certificates ▸ Authorities ▸ Import, then pick the exported file."),
        },
        _ => new[]
        {
            new SetupStep("Import the certificate manually",
                "",
                $"The root certificate is at {pemPath}. Add it to whatever trust store your platform uses."),
        },
    };

    /// <summary>The steps that route traffic on this machine through the proxy.</summary>
    public static IReadOnlyList<SetupStep> ProxySteps(string host, int port)
    {
        string url = $"http://{host}:{port}";
        return Current switch
        {
            HostPlatform.Windows => new[]
            {
                new SetupStep("System proxy",
                    "",
                    "The \"Set system proxy\" option does this automatically while capture is running."),
                new SetupStep("One command prompt only",
                    $"set HTTP_PROXY={url}&& set HTTPS_PROXY={url}",
                    "For console tools that read the standard variables."),
            },
            HostPlatform.Linux => new[]
            {
                new SetupStep("This shell and anything launched from it",
                    $"export HTTP_PROXY={url} HTTPS_PROXY={url} NO_PROXY=localhost,127.0.0.1",
                    "Honoured by curl, wget, git, pip, npm and most CLI tooling."),
                new SetupStep("GNOME desktop applications",
                    $"gsettings set org.gnome.system.proxy mode 'manual' && " +
                    $"gsettings set org.gnome.system.proxy.http host '{host}' && " +
                    $"gsettings set org.gnome.system.proxy.http port {port} && " +
                    $"gsettings set org.gnome.system.proxy.https host '{host}' && " +
                    $"gsettings set org.gnome.system.proxy.https port {port}",
                    "The \"Set system proxy\" option does this for you when gsettings is present."),
                new SetupStep("Undo the desktop setting",
                    "gsettings set org.gnome.system.proxy mode 'none'",
                    "Capture stopping restores this automatically."),
            },
            HostPlatform.MacOS => new[]
            {
                new SetupStep("This shell and anything launched from it",
                    $"export HTTP_PROXY={url} HTTPS_PROXY={url} NO_PROXY=localhost,127.0.0.1",
                    "Honoured by curl, git, pip, npm and most CLI tooling."),
                new SetupStep("Wi-Fi interface, system-wide",
                    $"sudo networksetup -setwebproxy Wi-Fi {host} {port} && " +
                    $"sudo networksetup -setsecurewebproxy Wi-Fi {host} {port}",
                    "Replace Wi-Fi with your active service from networksetup -listallnetworkservices."),
                new SetupStep("Undo it",
                    "sudo networksetup -setwebproxystate Wi-Fi off && sudo networksetup -setsecurewebproxystate Wi-Fi off",
                    ""),
            },
            _ => new[]
            {
                new SetupStep("Point your client at the proxy",
                    $"export HTTP_PROXY={url} HTTPS_PROXY={url}",
                    ""),
            },
        };
    }

    /// <summary>Renders a step list as plain text for a dialog or the clipboard.</summary>
    public static string Render(IReadOnlyList<SetupStep> steps)
    {
        var sb = new StringBuilder();
        foreach (var step in steps)
        {
            sb.Append("• ").AppendLine(step.Title);
            if (!string.IsNullOrEmpty(step.Command)) sb.Append("    ").AppendLine(step.Command);
            if (!string.IsNullOrEmpty(step.Note)) sb.Append("    ").AppendLine(step.Note);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ---- Process helper ------------------------------------------------------

    /// <summary>
    /// Runs a read-only query command and returns its output, or null if it could
    /// not be run. Nothing here ever runs a privileged command on the user's
    /// behalf — elevation is always left to a command the user chooses to paste.
    /// </summary>
    internal static string? RunCapture(string file, string arguments, int timeoutMs = 4000)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return null;

            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }
            return output;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// The GNOME proxy settings, driven through <c>gsettings</c>. This is the one
/// system-proxy mechanism outside Windows that is both per-user and reversible,
/// so it is safe to toggle automatically while capture runs.
/// </summary>
public static class GSettings
{
    private const string Schema = "org.gnome.system.proxy";

    private static bool? _available;

    public static bool IsAvailable
    {
        get
        {
            _available ??= PlatformIntegration.RunCapture("gsettings", $"get {Schema} mode") is not null;
            return _available.Value;
        }
    }

    public static void Enable(string host, int port)
    {
        Set($"{Schema}.http", "host", $"'{host}'");
        Set($"{Schema}.http", "port", port.ToString());
        Set($"{Schema}.https", "host", $"'{host}'");
        Set($"{Schema}.https", "port", port.ToString());
        Set(Schema, "ignore-hosts", "\"['localhost', '127.0.0.1', '::1']\"");
        Set(Schema, "mode", "'manual'");
    }

    public static void Disable() => Set(Schema, "mode", "'none'");

    public static bool IsEnabled() =>
        PlatformIntegration.RunCapture("gsettings", $"get {Schema} mode")?.Contains("manual") == true;

    private static void Set(string schema, string key, string value) =>
        PlatformIntegration.RunCapture("gsettings", $"set {schema} {key} {value}");
}
