using System.Text;

namespace HttpSpy.Core.Proxy.Http2;

/// <summary>A decoded HPACK header field (name/value pair).</summary>
public readonly struct HpackHeader
{
    public HpackHeader(string name, string value) { Name = name; Value = value; }
    public string Name { get; }
    public string Value { get; }
    public override string ToString() => $"{Name}: {Value}";
}

/// <summary>The HPACK static table (RFC 7541, Appendix A), 1-indexed.</summary>
internal static class HpackStatic
{
    public static readonly (string Name, string Value)[] Table =
    {
        ("", ""), // index 0 unused
        (":authority", ""),
        (":method", "GET"),
        (":method", "POST"),
        (":path", "/"),
        (":path", "/index.html"),
        (":scheme", "http"),
        (":scheme", "https"),
        (":status", "200"),
        (":status", "204"),
        (":status", "206"),
        (":status", "304"),
        (":status", "400"),
        (":status", "404"),
        (":status", "500"),
        ("accept-charset", ""),
        ("accept-encoding", "gzip, deflate"),
        ("accept-language", ""),
        ("accept-ranges", ""),
        ("accept", ""),
        ("access-control-allow-origin", ""),
        ("age", ""),
        ("allow", ""),
        ("authorization", ""),
        ("cache-control", ""),
        ("content-disposition", ""),
        ("content-encoding", ""),
        ("content-language", ""),
        ("content-length", ""),
        ("content-location", ""),
        ("content-range", ""),
        ("content-type", ""),
        ("cookie", ""),
        ("date", ""),
        ("etag", ""),
        ("expect", ""),
        ("expires", ""),
        ("from", ""),
        ("host", ""),
        ("if-match", ""),
        ("if-modified-since", ""),
        ("if-none-match", ""),
        ("if-range", ""),
        ("if-unmodified-since", ""),
        ("last-modified", ""),
        ("link", ""),
        ("location", ""),
        ("max-forwards", ""),
        ("proxy-authenticate", ""),
        ("proxy-authorization", ""),
        ("range", ""),
        ("referer", ""),
        ("refresh", ""),
        ("retry-after", ""),
        ("server", ""),
        ("set-cookie", ""),
        ("strict-transport-security", ""),
        ("transfer-encoding", ""),
        ("user-agent", ""),
        ("vary", ""),
        ("via", ""),
        ("www-authenticate", ""),
    };

    public const int Count = 61;
}

/// <summary>
/// Integer representation per RFC 7541 §5.1 (N-bit prefix). Shared by encoder
/// and decoder.
/// </summary>
internal static class HpackInteger
{
    public static int Decode(ReadOnlySpan<byte> data, ref int offset, int prefixBits)
    {
        int mask = (1 << prefixBits) - 1;
        int value = data[offset] & mask;
        offset++;
        if (value < mask) return value;

        int shift = 0;
        while (true)
        {
            if (offset >= data.Length) throw new InvalidDataException("HPACK integer truncated");
            byte b = data[offset++];
            value += (b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0) break;
            if (shift > 28) throw new InvalidDataException("HPACK integer too large");
        }
        return value;
    }

    public static void Encode(List<byte> output, int value, int prefixBits, int prefixFlags)
    {
        int mask = (1 << prefixBits) - 1;
        if (value < mask)
        {
            output.Add((byte)(prefixFlags | value));
            return;
        }
        output.Add((byte)(prefixFlags | mask));
        value -= mask;
        while (value >= 0x80)
        {
            output.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        output.Add((byte)value);
    }
}

/// <summary>
/// HPACK decoder (RFC 7541) with a per-connection dynamic table. One instance
/// is used per direction (one for client→server requests, one for responses).
/// </summary>
public sealed class HpackDecoder
{
    private readonly LinkedList<(string Name, string Value)> _dynamic = new();
    private int _dynamicSize;
    private int _maxDynamicSize;

    public HpackDecoder(int maxDynamicTableSize = 4096) => _maxDynamicSize = maxDynamicTableSize;

    public void SetMaxDynamicTableSize(int size)
    {
        _maxDynamicSize = size;
        Evict();
    }

    public List<HpackHeader> Decode(ReadOnlySpan<byte> block)
    {
        var headers = new List<HpackHeader>();
        int offset = 0;
        while (offset < block.Length)
        {
            byte first = block[offset];
            if ((first & 0x80) != 0)
            {
                // 6.1 Indexed Header Field
                int index = HpackInteger.Decode(block, ref offset, 7);
                if (index == 0) throw new InvalidDataException("HPACK: index 0 is invalid");
                var (name, value) = Lookup(index);
                headers.Add(new HpackHeader(name, value));
            }
            else if ((first & 0x40) != 0)
            {
                // 6.2.1 Literal Header Field with Incremental Indexing
                int index = HpackInteger.Decode(block, ref offset, 6);
                string name = index == 0 ? ReadString(block, ref offset) : Lookup(index).Name;
                string value = ReadString(block, ref offset);
                headers.Add(new HpackHeader(name, value));
                AddDynamic(name, value);
            }
            else if ((first & 0x20) != 0)
            {
                // 6.3 Dynamic Table Size Update
                int newSize = HpackInteger.Decode(block, ref offset, 5);
                if (newSize > _maxDynamicSize)
                    throw new InvalidDataException("HPACK: table size update exceeds limit");
                _maxDynamicSize = newSize;
                Evict();
            }
            else
            {
                // 6.2.2 / 6.2.3 Literal without indexing / never indexed
                int index = HpackInteger.Decode(block, ref offset, 4);
                string name = index == 0 ? ReadString(block, ref offset) : Lookup(index).Name;
                string value = ReadString(block, ref offset);
                headers.Add(new HpackHeader(name, value));
            }
        }
        return headers;
    }

    private (string Name, string Value) Lookup(int index)
    {
        if (index <= HpackStatic.Count) return HpackStatic.Table[index];
        int dynIndex = index - HpackStatic.Count - 1;
        if (dynIndex < 0 || dynIndex >= _dynamic.Count)
            throw new InvalidDataException($"HPACK: index {index} out of range");
        var node = _dynamic.First!;
        for (int i = 0; i < dynIndex; i++) node = node.Next!;
        return node.Value;
    }

    private void AddDynamic(string name, string value)
    {
        int entrySize = Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value) + 32;
        if (entrySize > _maxDynamicSize)
        {
            _dynamic.Clear();
            _dynamicSize = 0;
            return;
        }
        _dynamic.AddFirst((name, value));
        _dynamicSize += entrySize;
        Evict();
    }

    private void Evict()
    {
        while (_dynamicSize > _maxDynamicSize && _dynamic.Last is not null)
        {
            var (n, v) = _dynamic.Last.Value;
            _dynamicSize -= Encoding.UTF8.GetByteCount(n) + Encoding.UTF8.GetByteCount(v) + 32;
            _dynamic.RemoveLast();
        }
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int offset)
    {
        bool huffman = (data[offset] & 0x80) != 0;
        int length = HpackInteger.Decode(data, ref offset, 7);
        if (offset + length > data.Length) throw new InvalidDataException("HPACK string truncated");
        var slice = data.Slice(offset, length);
        offset += length;
        byte[] bytes = huffman ? HpackHuffman.Decode(slice) : slice.ToArray();
        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>
/// HPACK encoder. Emits header fields as literals (Huffman-coded when smaller),
/// without using incremental indexing. This is fully spec-compliant and keeps
/// the encoder stateless, which is robust for a proxy re-emitting headers.
/// </summary>
public sealed class HpackEncoder
{
    public byte[] Encode(IEnumerable<HpackHeader> headers)
    {
        var output = new List<byte>(256);
        foreach (var h in headers)
        {
            // 6.2.2 Literal Header Field without Indexing, new name (prefix 0000).
            output.Add(0x00);
            WriteString(output, h.Name);
            WriteString(output, h.Value);
        }
        return output.ToArray();
    }

    private static void WriteString(List<byte> output, string value)
    {
        byte[] raw = Encoding.UTF8.GetBytes(value);
        int huffLen = HpackHuffman.EncodedLength(raw);
        if (huffLen < raw.Length)
        {
            byte[] coded = HpackHuffman.Encode(raw);
            HpackInteger.Encode(output, coded.Length, 7, 0x80);
            output.AddRange(coded);
        }
        else
        {
            HpackInteger.Encode(output, raw.Length, 7, 0x00);
            output.AddRange(raw);
        }
    }
}
