using System.Security.Cryptography.X509Certificates;

namespace HttpSpy.Core.Proxy;

/// <summary>
/// A client certificate to present to origins matching a host pattern.
/// </summary>
public sealed class ClientCertificateBinding
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Host pattern: an exact host, a leading-dot suffix (<c>.example.com</c>),
    /// a <c>*</c> wildcard, or <c>*</c> for every host.
    /// </summary>
    public string HostPattern { get; set; } = "*";

    /// <summary>Path to a PKCS#12 (.pfx/.p12) or PEM certificate file.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Password for the PKCS#12 file, if it has one.</summary>
    public string? Password { get; set; }

    /// <summary>Path to the private key when the certificate is PEM and the key is separate.</summary>
    public string? KeyPath { get; set; }

    public ClientCertificateBinding Clone() => new()
    {
        Enabled = Enabled,
        HostPattern = HostPattern,
        Path = Path,
        Password = Password,
        KeyPath = KeyPath,
    };

    /// <summary>
    /// Parses the editable list form: one binding per line, as
    /// <c>host = /path/to/cert.pfx</c> with an optional <c>; password</c>. A bare
    /// path with no host applies to every origin, which is what a
    /// single-certificate setup wants.
    /// </summary>
    public static List<ClientCertificateBinding> Parse(string? text)
    {
        var bindings = new List<ClientCertificateBinding>();
        if (string.IsNullOrWhiteSpace(text)) return bindings;

        foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            string? password = null;
            int semi = line.LastIndexOf(';');
            if (semi > 0)
            {
                password = line[(semi + 1)..].Trim();
                line = line[..semi].Trim();
            }

            string host = "*";
            string path = line;
            int eq = line.IndexOf('=');

            // "C:\certs\client.pfx" has no '=' but a Windows path does contain a
            // colon, so only '=' may introduce a host.
            if (eq > 0)
            {
                host = line[..eq].Trim();
                path = line[(eq + 1)..].Trim();
            }

            if (path.Length == 0) continue;
            bindings.Add(new ClientCertificateBinding
            {
                HostPattern = host.Length == 0 ? "*" : host,
                Path = path,
                Password = string.IsNullOrEmpty(password) ? null : password,
            });
        }
        return bindings;
    }

    /// <summary>Renders bindings back into the editable form.</summary>
    public static string Format(IEnumerable<ClientCertificateBinding> bindings) =>
        string.Join(Environment.NewLine, bindings.Select(b =>
            b.HostPattern + " = " + b.Path + (string.IsNullOrEmpty(b.Password) ? "" : " ; " + b.Password)));

    /// <summary>True when this binding applies to <paramref name="host"/>.</summary>
    public bool Matches(string host)
    {
        if (!Enabled || string.IsNullOrEmpty(Path)) return false;

        var pattern = HostPattern?.Trim();
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;

        if (pattern.StartsWith('.'))
            return host.EndsWith(pattern, StringComparison.OrdinalIgnoreCase) ||
                   host.Equals(pattern[1..], StringComparison.OrdinalIgnoreCase);

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
            return host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) ||
                   host.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase);

        return host.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Loads and caches the client certificates presented on upstream TLS
/// handshakes (mutual TLS).
///
/// An API that requires a client certificate cannot be debugged without this at
/// all — the origin aborts the handshake and the transaction never reaches the
/// grid, which looks like a proxy bug rather than a missing credential.
/// Certificates are loaded once and cached by path, since a handshake happens
/// per connection and reading a PFX from disk each time would be needless work.
/// </summary>
public sealed class ClientCertificateStore
{
    public static ClientCertificateStore Shared { get; } = new();

    private readonly Dictionary<string, X509Certificate2?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Set when a certificate fails to load, so the UI can say why.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// The certificate configured for <paramref name="host"/>, or null when none
    /// applies. The first matching binding wins, so a specific host can be
    /// listed above a catch-all.
    /// </summary>
    public X509Certificate2? Resolve(IReadOnlyList<ClientCertificateBinding> bindings, string host)
    {
        if (bindings.Count == 0) return null;

        foreach (var binding in bindings)
        {
            if (!binding.Matches(host)) continue;
            var certificate = Load(binding);
            if (certificate is not null) return certificate;
        }
        return null;
    }

    private X509Certificate2? Load(ClientCertificateBinding binding)
    {
        string key = binding.Path + "|" + binding.KeyPath;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;

            X509Certificate2? certificate = null;
            try
            {
                if (!File.Exists(binding.Path))
                {
                    LastError = $"Client certificate not found: {binding.Path}";
                }
                else if (!string.IsNullOrEmpty(binding.KeyPath))
                {
                    certificate = X509Certificate2.CreateFromPemFile(binding.Path, binding.KeyPath);
                }
                else if (binding.Path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
                         binding.Path.EndsWith(".crt", StringComparison.OrdinalIgnoreCase))
                {
                    // A PEM bundle can carry the key alongside the certificate.
                    certificate = X509Certificate2.CreateFromPemFile(binding.Path);
                }
                else
                {
                    certificate = new X509Certificate2(binding.Path, binding.Password);
                }

                if (certificate is not null && !certificate.HasPrivateKey)
                {
                    LastError = $"Client certificate has no private key: {binding.Path}";
                    certificate.Dispose();
                    certificate = null;
                }
            }
            catch (Exception ex)
            {
                LastError = $"Could not load {binding.Path}: {ex.Message}";
                certificate = null;
            }

            // Negative results are cached too: a missing file should not be
            // retried on every single connection.
            _cache[key] = certificate;
            return certificate;
        }
    }

    /// <summary>Drops the cache so edited or replaced files are picked up.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            foreach (var certificate in _cache.Values) certificate?.Dispose();
            _cache.Clear();
            LastError = null;
        }
    }
}
