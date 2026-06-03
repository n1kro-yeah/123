using System.Text;
using HttpSpy.Core.Proxy.Transparent;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>
/// Tests the transparent-capture support pieces that are platform-independent:
/// the TLS ClientHello SNI parser and the PrefixedStream replay wrapper.
/// </summary>
public class TransparentTests
{
    [Fact]
    public void Sni_Parses_HostName_From_ClientHello()
    {
        var hello = BuildClientHello("example.com");
        Assert.Equal("example.com", TlsClientHello.TryParseSni(hello));
    }

    [Fact]
    public void Sni_Parses_Long_HostName()
    {
        var hello = BuildClientHello("api.really-long-subdomain.httpspy.example.org");
        Assert.Equal("api.really-long-subdomain.httpspy.example.org", TlsClientHello.TryParseSni(hello));
    }

    [Fact]
    public void Sni_Returns_Null_For_NonTls()
    {
        Assert.Null(TlsClientHello.TryParseSni(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n")));
        Assert.Null(TlsClientHello.TryParseSni(Array.Empty<byte>()));
        Assert.Null(TlsClientHello.TryParseSni(new byte[] { 0x16, 0x03 })); // truncated
    }

    [Fact]
    public async Task PrefixedStream_Replays_Prefix_Then_Inner()
    {
        var prefix = Encoding.ASCII.GetBytes("HEAD");
        using var inner = new MemoryStream(Encoding.ASCII.GetBytes("TAIL"));
        using var ps = new PrefixedStream(prefix, inner);

        var buf = new byte[8];
        int total = 0, n;
        while (total < 8 && (n = await ps.ReadAsync(buf.AsMemory(total, 8 - total))) > 0)
            total += n;

        Assert.Equal("HEADTAIL", Encoding.ASCII.GetString(buf, 0, total));
    }

    /// <summary>Builds a minimal but well-formed TLS 1.2 ClientHello carrying an SNI extension.</summary>
    private static byte[] BuildClientHello(string host)
    {
        var hostBytes = Encoding.ASCII.GetBytes(host);

        // server_name extension body.
        var sni = new List<byte>();
        sni.Add(0x00); // name_type = host_name
        sni.Add((byte)(hostBytes.Length >> 8));
        sni.Add((byte)hostBytes.Length);
        sni.AddRange(hostBytes);
        var serverNameList = new List<byte>();
        serverNameList.Add((byte)(sni.Count >> 8));
        serverNameList.Add((byte)sni.Count);
        serverNameList.AddRange(sni);

        var ext = new List<byte>();
        ext.AddRange(new byte[] { 0x00, 0x00 }); // extension type = server_name
        ext.Add((byte)(serverNameList.Count >> 8));
        ext.Add((byte)serverNameList.Count);
        ext.AddRange(serverNameList);

        var extensions = new List<byte>();
        extensions.Add((byte)(ext.Count >> 8));
        extensions.Add((byte)ext.Count);
        extensions.AddRange(ext);

        var body = new List<byte>();
        body.AddRange(new byte[] { 0x03, 0x03 }); // client_version TLS 1.2
        body.AddRange(new byte[32]);              // random
        body.Add(0x00);                            // session_id length
        body.AddRange(new byte[] { 0x00, 0x02, 0x13, 0x01 }); // cipher_suites (len 2 + one suite)
        body.AddRange(new byte[] { 0x01, 0x00 }); // compression_methods (len 1, null)
        body.AddRange(extensions);

        var handshake = new List<byte>();
        handshake.Add(0x01); // ClientHello
        handshake.Add((byte)(body.Count >> 16));
        handshake.Add((byte)(body.Count >> 8));
        handshake.Add((byte)body.Count);
        handshake.AddRange(body);

        var record = new List<byte>();
        record.Add(0x16);                          // handshake
        record.AddRange(new byte[] { 0x03, 0x01 }); // record version
        record.Add((byte)(handshake.Count >> 8));
        record.Add((byte)handshake.Count);
        record.AddRange(handshake);
        return record.ToArray();
    }
}
