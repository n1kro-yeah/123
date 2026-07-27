using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HttpSpy.Core.Proxy;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>
/// Chaining through a proxy that wants credentials used to fail with nothing
/// but a generic CONNECT error, and an origin requiring a client certificate
/// could not be debugged at all. These cover both.
/// </summary>
public class UpstreamAuthTests
{
    // ---- Proxy authentication ------------------------------------------------

    [Fact]
    public void No_credentials_means_no_proxy_authorization_header()
    {
        var upstream = new Upstream(new ProxyOptions { UpstreamProxyHost = "proxy.test", UpstreamProxyPort = 8080 });
        Assert.Null(upstream.ProxyAuthorization());
    }

    [Fact]
    public void Credentials_produce_a_basic_header()
    {
        var options = new ProxyOptions
        {
            UpstreamProxyHost = "proxy.test",
            UpstreamProxyPort = 8080,
            UpstreamProxyUser = "alice",
            UpstreamProxyPassword = "s3cret",
        };

        var header = new Upstream(options).ProxyAuthorization();

        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret")), header);
    }

    [Fact]
    public void An_empty_password_is_still_a_valid_credential()
    {
        var options = new ProxyOptions { UpstreamProxyUser = "token-user", UpstreamProxyPassword = "" };
        Assert.True(options.HasUpstreamCredentials);
        Assert.StartsWith("Basic ", new Upstream(options).ProxyAuthorization());
    }

    [Fact]
    public void An_empty_user_means_no_credentials()
    {
        Assert.False(new ProxyOptions { UpstreamProxyPassword = "orphan" }.HasUpstreamCredentials);
    }

    [Fact]
    public void Absolute_form_is_only_required_for_plain_http_through_a_chain()
    {
        var chained = new Upstream(new ProxyOptions { UpstreamProxyHost = "proxy.test", UpstreamProxyPort = 8080 });
        Assert.True(chained.RequiresAbsoluteForm(tls: false));
        Assert.False(chained.RequiresAbsoluteForm(tls: true));   // TLS goes through CONNECT

        var direct = new Upstream(new ProxyOptions());
        Assert.False(direct.RequiresAbsoluteForm(tls: false));
    }

    // ---- Client certificate bindings ----------------------------------------

    [Theory]
    [InlineData("*", "api.example.com", true)]
    [InlineData("api.example.com", "api.example.com", true)]
    [InlineData("api.example.com", "other.example.com", false)]
    [InlineData("API.EXAMPLE.COM", "api.example.com", true)]
    [InlineData(".example.com", "api.example.com", true)]
    [InlineData(".example.com", "example.com", true)]
    [InlineData(".example.com", "example.org", false)]
    [InlineData("*.example.com", "api.example.com", true)]
    [InlineData("*.example.com", "example.com", true)]
    [InlineData("*.example.com", "notexample.com", false)]
    public void Host_patterns_match_the_way_they_read(string pattern, string host, bool expected)
    {
        var binding = new ClientCertificateBinding { HostPattern = pattern, Path = "/tmp/cert.pfx" };
        Assert.Equal(expected, binding.Matches(host));
    }

    [Fact]
    public void A_disabled_or_pathless_binding_never_matches()
    {
        Assert.False(new ClientCertificateBinding { Path = "/tmp/c.pfx", Enabled = false }.Matches("x.test"));
        Assert.False(new ClientCertificateBinding { Path = "" }.Matches("x.test"));
    }

    [Fact]
    public void Bindings_clone_independently()
    {
        var original = new ClientCertificateBinding { HostPattern = "a.test", Path = "/p", Password = "pw" };
        var copy = original.Clone();
        copy.HostPattern = "b.test";

        Assert.Equal("a.test", original.HostPattern);
        Assert.Equal("/p", copy.Path);
        Assert.Equal("pw", copy.Password);
    }

    // ---- Certificate loading -------------------------------------------------

    [Fact]
    public void A_real_pkcs12_certificate_loads_and_keeps_its_key()
    {
        var dir = Path.Combine(Path.GetTempPath(), "httpspy-mtls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "client.pfx");
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest("CN=HttpSpy Test Client", rsa,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddDays(30));
                File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, "pw"));
            }

            var store = new ClientCertificateStore();
            var bindings = new List<ClientCertificateBinding>
            {
                new() { HostPattern = "api.test", Path = path, Password = "pw" },
            };

            var resolved = store.Resolve(bindings, "api.test");

            Assert.NotNull(resolved);
            Assert.True(resolved!.HasPrivateKey, "a client certificate without its key is useless for mTLS");
            Assert.Contains("HttpSpy Test Client", resolved.Subject);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_first_matching_binding_wins()
    {
        var store = new ClientCertificateStore();
        var bindings = new List<ClientCertificateBinding>
        {
            new() { HostPattern = "api.test", Path = "/does/not/exist-specific.pfx" },
            new() { HostPattern = "*", Path = "/does/not/exist-catchall.pfx" },
        };

        // Neither loads, but the resolution order is what matters: the specific
        // entry is tried first, which is why listing it above a catch-all works.
        Assert.Null(store.Resolve(bindings, "api.test"));
        Assert.Contains("exist-catchall", store.LastError);
    }

    [Fact]
    public void A_missing_file_is_reported_rather_than_thrown()
    {
        var store = new ClientCertificateStore();
        var bindings = new List<ClientCertificateBinding> { new() { Path = "/no/such/client.pfx" } };

        Assert.Null(store.Resolve(bindings, "anything.test"));
        Assert.NotNull(store.LastError);
        Assert.Contains("client.pfx", store.LastError);
    }

    [Fact]
    public void No_bindings_means_no_certificate_and_no_work()
    {
        var store = new ClientCertificateStore();
        Assert.Null(store.Resolve(Array.Empty<ClientCertificateBinding>(), "x.test"));
        Assert.Null(store.LastError);
    }

    // ---- The editable text form ----------------------------------------------

    [Fact]
    public void Bindings_parse_from_the_one_line_per_entry_form()
    {
        var parsed = ClientCertificateBinding.Parse(
            "api.example.com = /certs/api.pfx ; hunter2\n" +
            "# a comment\n" +
            "\n" +
            "/certs/fallback.pfx\n");

        Assert.Equal(2, parsed.Count);
        Assert.Equal("api.example.com", parsed[0].HostPattern);
        Assert.Equal("/certs/api.pfx", parsed[0].Path);
        Assert.Equal("hunter2", parsed[0].Password);

        // A bare path with no host applies everywhere, which is what a
        // single-certificate setup wants.
        Assert.Equal("*", parsed[1].HostPattern);
        Assert.Equal("/certs/fallback.pfx", parsed[1].Path);
        Assert.Null(parsed[1].Password);
    }

    [Fact]
    public void A_windows_path_is_not_mistaken_for_a_host_binding()
    {
        var parsed = ClientCertificateBinding.Parse(@"C:\certs\client.pfx");
        var binding = Assert.Single(parsed);
        Assert.Equal("*", binding.HostPattern);
        Assert.Equal(@"C:\certs\client.pfx", binding.Path);
    }

    [Fact]
    public void Bindings_round_trip_through_the_text_form()
    {
        const string text = "api.example.com = /certs/api.pfx ; pw";
        var parsed = ClientCertificateBinding.Parse(text);
        var again = ClientCertificateBinding.Parse(
            ClientCertificateBinding.Format(parsed));

        Assert.Equal(parsed.Count, again.Count);
        Assert.Equal(parsed[0].HostPattern, again[0].HostPattern);
        Assert.Equal(parsed[0].Path, again[0].Path);
        Assert.Equal(parsed[0].Password, again[0].Password);
    }

    [Fact]
    public void An_empty_list_parses_to_nothing()
    {
        Assert.Empty(ClientCertificateBinding.Parse("   \n\n"));
    }
}
