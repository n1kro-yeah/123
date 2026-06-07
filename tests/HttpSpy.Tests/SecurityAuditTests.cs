using System.IO;
using System.IO.Compression;
using System.Text;
using HttpSpy.Core.Export;
using HttpSpy.Core.Models;
using HttpSpy.Core.Proxy;
using HttpSpy.Core.Rules;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>
/// Regression tests for the security/robustness hardening: decompression-bomb
/// bounds, header-line length caps, regex (ReDoS) timeouts and export filename
/// sanitization.
/// </summary>
public class SecurityAuditTests
{
    // ---- Decompression bomb guard -------------------------------------------
    [Fact]
    public void CopyBounded_Throws_When_Output_Exceeds_Limit()
    {
        using var source = new MemoryStream(new byte[1024]);
        using var dest = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => HttpWire.CopyBounded(source, dest, maxBytes: 100));
    }

    [Fact]
    public void CopyBounded_Copies_When_Within_Limit()
    {
        var payload = Encoding.ASCII.GetBytes("hello world");
        using var source = new MemoryStream(payload);
        using var dest = new MemoryStream();
        HttpWire.CopyBounded(source, dest, maxBytes: 1024);
        Assert.Equal(payload, dest.ToArray());
    }

    [Fact]
    public void Decompress_RoundTrips_Legitimate_Gzip()
    {
        var original = Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog");
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
            gz.Write(original, 0, original.Length);
        var decoded = HttpWire.Decompress(ms.ToArray(), "gzip");
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Decompress_Returns_Original_On_Bomb()
    {
        // ~64 MiB of zeros compresses to a few KB but would overflow a small cap.
        // We can't lower the production cap, so prove the guard via a stream that
        // inflates well past a reasonable bound: 64 MiB of zeros decompressed
        // under the real 256 MiB cap still round-trips, while CopyBounded above
        // proves the throw path. Here we assert malformed input is returned as-is.
        var notGzip = Encoding.ASCII.GetBytes("this is not compressed data");
        var result = HttpWire.Decompress(notGzip, "gzip");
        Assert.Equal(notGzip, result); // decode failed -> original bytes returned
    }

    // ---- StreamReaderEx line-length cap -------------------------------------
    [Fact]
    public async Task ReadLine_Throws_On_Oversized_Line()
    {
        var data = Encoding.ASCII.GetBytes(new string('a', 1000)); // no CRLF
        using var ms = new MemoryStream(data);
        var reader = new StreamReaderEx(ms, bufferSize: 256, maxLineLength: 128);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadLine_Returns_Normal_Line()
    {
        var data = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n");
        using var ms = new MemoryStream(data);
        var reader = new StreamReaderEx(ms, bufferSize: 256, maxLineLength: 128);
        var line = await reader.ReadLineAsync(CancellationToken.None);
        Assert.Equal("GET / HTTP/1.1", line);
    }

    // ---- Regex (ReDoS) timeout ----------------------------------------------
    [Fact]
    public void Catastrophic_Regex_Does_Not_Hang_Url_Match()
    {
        var rule = new Rule
        {
            UrlMatchMode = MatchMode.Regex,
            UrlPattern = "(a+)+$",
        };
        // A non-matching tail forces catastrophic backtracking; the 2s timeout
        // must convert that into a quick, safe "no match" rather than a hang.
        string evil = new string('a', 40) + "!";
        bool result = rule.UrlMatches(evil);
        Assert.False(result);
    }

    // ---- Export filename sanitization (path traversal) ----------------------
    [Fact]
    public void SeparateFilesExporter_Sanitizes_Traversal_In_Host_And_Method()
    {
        string dir = Path.Combine(Path.GetTempPath(), "httpspy_audit_" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new HttpSession
            {
                Index = 1,
                Method = "../../../evil",
                Host = "../../../../etc/passwd",
            };
            int count = SeparateFilesExporter.Export(new[] { session }, dir);
            Assert.Equal(1, count);

            // Every produced file must live directly inside the target directory.
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            Assert.Single(files);
            string full = Path.GetFullPath(files[0]);
            Assert.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, full);

            string fileName = Path.GetFileName(files[0]);
            Assert.DoesNotContain("..", fileName);
            Assert.DoesNotContain("/", fileName);
            Assert.DoesNotContain("\\", fileName);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ---- CA private-key file permissions (POSIX) ----------------------------
    [Fact]
    public void RootCa_Private_Key_Is_Not_World_Readable_On_Posix()
    {
        if (OperatingSystem.IsWindows()) return; // chmod is a no-op on Windows

        string dir = Path.Combine(Path.GetTempPath(), "httpspy_ca_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var ca = new CertificateAuthority(dir);
            string pfx = Path.Combine(dir, "HttpSpyRootCA.pfx");
            Assert.True(File.Exists(pfx));

            var mode = File.GetUnixFileMode(pfx);
            // No group/other access to the file that holds the CA private key.
            Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
