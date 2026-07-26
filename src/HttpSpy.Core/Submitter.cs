using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using HttpSpy.Core.Models;

namespace HttpSpy.Core;

/// <summary>
/// Composes and sends an HTTP request (the "Submitter" / request builder), then
/// captures the response into a new <see cref="HttpSession"/>. Used both for the
/// manual request builder and for replaying / editing captured sessions.
/// </summary>
public sealed class Submitter : IDisposable
{
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _client;

    public Submitter(bool ignoreCertErrors = true, TimeSpan? timeout = null)
    {
        _handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        };
        if (ignoreCertErrors)
            _handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        _client = new HttpClient(_handler) { Timeout = timeout ?? TimeSpan.FromSeconds(100) };
    }

    /// <summary>
    /// Headers the transport owns. Replaying a captured request would otherwise
    /// carry the original <c>Content-Length</c> across to an edited body, and the
    /// hop-by-hop headers are meaningless on a fresh connection.
    /// </summary>
    private static readonly HashSet<string> TransportOwnedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Connection", "Proxy-Connection", "Keep-Alive",
        "Transfer-Encoding", "Upgrade", "TE", "Trailer",
    };

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }

    /// <summary>Builds a fresh request spec from an existing captured session.</summary>
    public static RequestSpec SpecFromSession(HttpSession s)
    {
        var spec = new RequestSpec
        {
            Method = s.Method,
            Url = s.FullUrl,
            HttpVersion = s.HttpVersion,
            Body = s.RequestBodyText,
        };
        foreach (var h in s.RequestHeaders) spec.Headers.Add(new HttpHeader(h.Name, h.Value));
        return spec;
    }

    public async Task<HttpSession> SendAsync(RequestSpec spec, CancellationToken ct = default)
    {
        var session = new HttpSession { IsReplay = true, Method = spec.Method, State = SessionState.SentToServer };
        var sw = Stopwatch.StartNew();
        try
        {
            var uri = new Uri(spec.Url);
            session.Scheme = uri.Scheme;
            session.Host = uri.Host;
            session.RemotePort = uri.Port;
            session.Path = uri.AbsolutePath;
            session.QueryString = uri.Query.TrimStart('?');
            session.Url = spec.Url;
            session.IsTls = uri.Scheme == "https";
            session.Kind = session.IsTls ? SessionKind.Https : SessionKind.Http;

            using var request = new HttpRequestMessage(new HttpMethod(spec.Method), uri);
            if (Version.TryParse(spec.HttpVersion?.Replace("HTTP/", "", StringComparison.OrdinalIgnoreCase),
                    out var version))
            {
                request.Version = version;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            }

            byte[] bodyBytes = Encoding.UTF8.GetBytes(spec.Body ?? string.Empty);
            foreach (var h in spec.Headers) session.RequestHeaders.Add(h.Name, h.Value);

            if (bodyBytes.Length > 0 || HasBody(spec.Method))
            {
                request.Content = new ByteArrayContent(bodyBytes);
                session.RequestBody = bodyBytes;
            }

            foreach (var h in spec.Headers)
            {
                // Content-Length in particular must not be copied: the body may
                // have been edited since capture, and HttpClient computes it.
                if (TransportOwnedHeaders.Contains(h.Name)) continue;

                if (IsContentHeader(h.Name))
                    request.Content?.Headers.TryAddWithoutValidation(h.Name, h.Value);
                else
                    request.Headers.TryAddWithoutValidation(h.Name, h.Value);
            }

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            session.StatusCode = (int)response.StatusCode;
            session.StatusText = response.ReasonPhrase ?? string.Empty;
            session.ResponseHttpVersion = "HTTP/" + response.Version;
            foreach (var h in response.Headers)
                foreach (var v in h.Value) session.ResponseHeaders.Add(h.Key, v);
            foreach (var h in response.Content.Headers)
                foreach (var v in h.Value) session.ResponseHeaders.Add(h.Key, v);

            session.ResponseBody = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            session.BytesReceived = session.ResponseBody.Length;
            // Rough but honest: request line + headers + body actually put on the wire.
            session.BytesSent = bodyBytes.LongLength +
                                session.RequestHeaders.Items.Sum(h => (long)h.Name.Length + h.Value.Length + 4) +
                                spec.Method.Length + uri.PathAndQuery.Length + 12;
            session.State = SessionState.Completed;
        }
        catch (Exception ex)
        {
            session.Error = ex.Message;
            session.State = SessionState.Faulted;
        }
        finally
        {
            sw.Stop();
            session.Timings.TotalMs = sw.Elapsed.TotalMilliseconds;
            session.EndTime = DateTime.Now;
        }
        return session;
    }

    private static bool HasBody(string method) =>
        method is "POST" or "PUT" or "PATCH" or "DELETE";

    private static bool IsContentHeader(string name) =>
        name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An editable request specification used by the Submitter.</summary>
public sealed class RequestSpec
{
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "https://";
    public string HttpVersion { get; set; } = "HTTP/1.1";
    public List<HttpHeader> Headers { get; set; } = new();
    public string Body { get; set; } = string.Empty;
}
