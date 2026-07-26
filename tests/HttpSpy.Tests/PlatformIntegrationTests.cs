using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HttpSpy.Core.Proxy;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>
/// Trust-store handling used to be Windows-only, which left the Linux and macOS
/// builds with a "not supported" dialog and no path forward. These cover the
/// platform-neutral pieces: PEM export, honest trust reporting, and instructions
/// that actually reference the file we wrote.
/// </summary>
public class PlatformIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "httpspy-platform-" + Guid.NewGuid().ToString("N"));
    private readonly X509Certificate2 _cert;

    public PlatformIntegrationTests()
    {
        Directory.CreateDirectory(_dir);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=HttpSpy Test Root", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        _cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    public void Dispose()
    {
        _cert.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ExportPem_writes_a_parseable_pem_file()
    {
        var path = Path.Combine(_dir, "root.pem");
        PlatformIntegration.ExportPem(_cert, path);

        var text = File.ReadAllText(path);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", text);
        Assert.EndsWith("-----END CERTIFICATE-----\n", text);

        // The whole point is that OpenSSL-style consumers can read it back.
        var reloaded = X509Certificate2.CreateFromPem(text);
        Assert.Equal(_cert.Thumbprint, reloaded.Thumbprint);
    }

    [Fact]
    public void ExportPem_wraps_the_body_so_strict_parsers_accept_it()
    {
        var path = Path.Combine(_dir, "wrapped.pem");
        PlatformIntegration.ExportPem(_cert, path);

        var lines = File.ReadAllLines(path);
        var body = lines.Where(l => !l.StartsWith("-----")).ToArray();
        Assert.NotEmpty(body);
        Assert.All(body, l => Assert.True(l.Length <= 76, $"line of {l.Length} chars exceeds the PEM width"));
        Assert.DoesNotContain(lines, l => l.Contains('\r'));
    }

    [Fact]
    public void ExportPem_body_matches_the_der_encoding()
    {
        var path = Path.Combine(_dir, "match.pem");
        PlatformIntegration.ExportPem(_cert, path);

        var flat = File.ReadAllText(path)
            .Replace("-----BEGIN CERTIFICATE-----", "")
            .Replace("-----END CERTIFICATE-----", "")
            .Replace("\n", "").Replace("\r", "");

        Assert.Equal(PlatformIntegration.Base64Body(_cert), flat);
    }

    [Fact]
    public void Current_platform_is_identified()
    {
        Assert.NotEqual(HostPlatform.Unknown, PlatformIntegration.Current);
        Assert.False(string.IsNullOrWhiteSpace(PlatformIntegration.PlatformName));
    }

    [Fact]
    public void Automatic_trust_install_is_claimed_only_on_windows()
    {
        Assert.Equal(OperatingSystem.IsWindows(), PlatformIntegration.CanInstallTrustAutomatically);
    }

    [Fact]
    public void QueryTrust_never_reports_trusted_for_a_certificate_nobody_installed()
    {
        // A freshly minted throwaway CA cannot be in any store; the only correct
        // answers are "not trusted" or "cannot tell".
        var state = PlatformIntegration.QueryTrust(_cert);
        Assert.NotEqual(TrustState.Trusted, state);
    }

    [Fact]
    public void TrustSteps_reference_the_exported_file()
    {
        var path = Path.Combine(_dir, "root.pem");
        PlatformIntegration.ExportPem(_cert, path);

        var steps = PlatformIntegration.TrustSteps(path);
        Assert.NotEmpty(steps);
        Assert.Contains(steps, s => s.Command.Contains(path, StringComparison.Ordinal));
        Assert.All(steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Title)));
    }

    [Fact]
    public void TrustSteps_cover_firefox_which_ignores_the_system_store()
    {
        var steps = PlatformIntegration.TrustSteps("/tmp/root.pem");
        Assert.Contains(steps, s =>
            s.Title.Contains("Firefox", StringComparison.OrdinalIgnoreCase) ||
            s.Note.Contains("Firefox", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProxySteps_include_the_configured_host_and_port()
    {
        var steps = PlatformIntegration.ProxySteps("127.0.0.1", 9090);
        Assert.NotEmpty(steps);
        Assert.Contains(steps, s => s.Command.Contains("9090", StringComparison.Ordinal));
        Assert.Contains(steps, s => s.Command.Contains("127.0.0.1", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_produces_something_pasteable()
    {
        var text = PlatformIntegration.Render(PlatformIntegration.ProxySteps("127.0.0.1", 8888));
        Assert.Contains("8888", text);
        Assert.StartsWith("•", text);
        Assert.False(text.EndsWith("\n"), "trailing blank lines make the dialog look broken");
    }

    [Fact]
    public void RunCapture_returns_null_for_a_command_that_does_not_exist()
    {
        Assert.Null(PlatformIntegration.RunCapture("httpspy-no-such-binary-xyz", "--version"));
    }
}
