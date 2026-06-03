using System.Text;
using HttpSpy.Core.Proxy.Http2;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>
/// HPACK conformance tests using the worked examples from RFC 7541, Appendix C.
/// These exercise integer coding, the static and dynamic tables and the Huffman
/// codec against the exact byte sequences published in the specification.
/// </summary>
public class HpackTests
{
    private static byte[] Hex(string s)
    {
        s = s.Replace(" ", "").Replace("\n", "");
        var bytes = new byte[s.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return bytes;
    }

    [Fact]
    public void Huffman_Decodes_WwwExampleCom()
    {
        var decoded = HpackHuffman.Decode(Hex("f1e3 c2e5 f23a 6ba0 ab90 f4ff"));
        Assert.Equal("www.example.com", Encoding.ASCII.GetString(decoded));
    }

    [Fact]
    public void Huffman_Encodes_WwwExampleCom()
    {
        var encoded = HpackHuffman.Encode(Encoding.ASCII.GetBytes("www.example.com"));
        Assert.Equal(Hex("f1e3 c2e5 f23a 6ba0 ab90 f4ff"), encoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("no-cache")]
    [InlineData("Mon, 21 Oct 2013 20:13:21 GMT")]
    [InlineData("https://www.example.com/some/very/long/path?with=query&and=stuff")]
    public void Huffman_RoundTrips(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        var roundTrip = HpackHuffman.Decode(HpackHuffman.Encode(raw));
        Assert.Equal(raw, roundTrip);
    }

    [Fact]
    public void Integer_RoundTrips_AcrossPrefixWidths()
    {
        foreach (int prefix in new[] { 4, 5, 6, 7 })
        {
            foreach (int value in new[] { 0, 1, 10, 30, 31, 127, 128, 255, 1337, 100000 })
            {
                var buffer = new List<byte>();
                HpackInteger.Encode(buffer, value, prefix, 0);
                var span = buffer.ToArray().AsSpan();
                int offset = 0;
                int decoded = HpackInteger.Decode(span, ref offset, prefix);
                Assert.Equal(value, decoded);
                Assert.Equal(buffer.Count, offset);
            }
        }
    }

    [Fact]
    public void Decodes_C31_FirstRequest_NoHuffman()
    {
        var decoder = new HpackDecoder();
        var headers = decoder.Decode(Hex("8286 8441 0f77 7777 2e65 7861 6d70 6c65 2e63 6f6d"));
        Assert.Equal(4, headers.Count);
        Assert.Equal(":method", headers[0].Name); Assert.Equal("GET", headers[0].Value);
        Assert.Equal(":scheme", headers[1].Name); Assert.Equal("http", headers[1].Value);
        Assert.Equal(":path", headers[2].Name); Assert.Equal("/", headers[2].Value);
        Assert.Equal(":authority", headers[3].Name); Assert.Equal("www.example.com", headers[3].Value);
    }

    [Fact]
    public void Decodes_C41_FirstRequest_Huffman()
    {
        var decoder = new HpackDecoder();
        var headers = decoder.Decode(Hex("8286 8441 8cf1 e3c2 e5f2 3a6b a0ab 90f4 ff"));
        Assert.Equal(":authority", headers[3].Name);
        Assert.Equal("www.example.com", headers[3].Value);
    }

    [Fact]
    public void Decodes_C61_FirstResponse_Huffman()
    {
        var decoder = new HpackDecoder();
        var headers = decoder.Decode(Hex(
            "4882 6402 5885 aec3 771a 4b61 96d0 7abe 9410 54d4 44a8 2005 9504 0b81 66e0 82a6 2d1b ff6e 919d 29ad 1718 63c7 8f0b 97c8 e9ae 82ae 43d3"));
        Assert.Equal(":status", headers[0].Name); Assert.Equal("302", headers[0].Value);
        Assert.Equal("cache-control", headers[1].Name); Assert.Equal("private", headers[1].Value);
        Assert.Equal("date", headers[2].Name); Assert.Equal("Mon, 21 Oct 2013 20:13:21 GMT", headers[2].Value);
        Assert.Equal("location", headers[3].Name); Assert.Equal("https://www.example.com", headers[3].Value);
    }

    [Fact]
    public void DynamicTable_PersistsAcrossRequests_C42_And_C43()
    {
        // A single decoder must carry the dynamic table across the request sequence.
        var decoder = new HpackDecoder();
        decoder.Decode(Hex("8286 8441 8cf1 e3c2 e5f2 3a6b a0ab 90f4 ff")); // C.4.1

        var second = decoder.Decode(Hex("8286 84be 5886 a8eb 1064 9cbf")); // C.4.2
        Assert.Equal("www.example.com", second[3].Value); // 'be' references the dynamic entry
        Assert.Equal("cache-control", second[4].Name);
        Assert.Equal("no-cache", second[4].Value);

        var third = decoder.Decode(Hex("8287 85bf 4088 25a8 49e9 5ba9 7d7f 8925 a849 e95b b8e8 b4bf")); // C.4.3
        Assert.Contains(third, h => h.Name == "custom-key" && h.Value == "custom-value");
        Assert.Contains(third, h => h.Name == ":authority" && h.Value == "www.example.com");
    }

    [Fact]
    public void Encoder_And_Decoder_RoundTrip()
    {
        var encoder = new HpackEncoder();
        var decoder = new HpackDecoder();
        var input = new[]
        {
            new HpackHeader(":method", "POST"),
            new HpackHeader(":scheme", "https"),
            new HpackHeader(":path", "/api/v1/resource?id=42"),
            new HpackHeader(":authority", "api.example.com"),
            new HpackHeader("content-type", "application/grpc"),
            new HpackHeader("user-agent", "HttpSpy/1.0 (test)"),
            new HpackHeader("x-custom", "value-with-üñîçödé"),
        };
        var decoded = decoder.Decode(encoder.Encode(input));
        Assert.Equal(input.Length, decoded.Count);
        for (int i = 0; i < input.Length; i++)
        {
            Assert.Equal(input[i].Name, decoded[i].Name);
            Assert.Equal(input[i].Value, decoded[i].Value);
        }
    }
}
