namespace HttpSpy.Core.Proxy.Http2;

/// <summary>
/// HPACK Huffman codec (RFC 7541, Appendix B). The code/length tables are the
/// canonical 257-symbol table from the RFC (256 octets + EOS). Used to
/// encode/decode Huffman-coded string literals in HPACK header blocks.
/// </summary>
internal static class HpackHuffman
{
    // Generated verbatim from RFC 7541 Appendix B (code aligned to LSB, length in bits).
    private static readonly uint[] Codes =
    {
        0x00001FF8, 0x007FFFD8, 0x0FFFFFE2, 0x0FFFFFE3, 0x0FFFFFE4, 0x0FFFFFE5, 0x0FFFFFE6, 0x0FFFFFE7,
        0x0FFFFFE8, 0x00FFFFEA, 0x3FFFFFFC, 0x0FFFFFE9, 0x0FFFFFEA, 0x3FFFFFFD, 0x0FFFFFEB, 0x0FFFFFEC,
        0x0FFFFFED, 0x0FFFFFEE, 0x0FFFFFEF, 0x0FFFFFF0, 0x0FFFFFF1, 0x0FFFFFF2, 0x3FFFFFFE, 0x0FFFFFF3,
        0x0FFFFFF4, 0x0FFFFFF5, 0x0FFFFFF6, 0x0FFFFFF7, 0x0FFFFFF8, 0x0FFFFFF9, 0x0FFFFFFA, 0x0FFFFFFB,
        0x00000014, 0x000003F8, 0x000003F9, 0x00000FFA, 0x00001FF9, 0x00000015, 0x000000F8, 0x000007FA,
        0x000003FA, 0x000003FB, 0x000000F9, 0x000007FB, 0x000000FA, 0x00000016, 0x00000017, 0x00000018,
        0x00000000, 0x00000001, 0x00000002, 0x00000019, 0x0000001A, 0x0000001B, 0x0000001C, 0x0000001D,
        0x0000001E, 0x0000001F, 0x0000005C, 0x000000FB, 0x00007FFC, 0x00000020, 0x00000FFB, 0x000003FC,
        0x00001FFA, 0x00000021, 0x0000005D, 0x0000005E, 0x0000005F, 0x00000060, 0x00000061, 0x00000062,
        0x00000063, 0x00000064, 0x00000065, 0x00000066, 0x00000067, 0x00000068, 0x00000069, 0x0000006A,
        0x0000006B, 0x0000006C, 0x0000006D, 0x0000006E, 0x0000006F, 0x00000070, 0x00000071, 0x00000072,
        0x000000FC, 0x00000073, 0x000000FD, 0x00001FFB, 0x0007FFF0, 0x00001FFC, 0x00003FFC, 0x00000022,
        0x00007FFD, 0x00000003, 0x00000023, 0x00000004, 0x00000024, 0x00000005, 0x00000025, 0x00000026,
        0x00000027, 0x00000006, 0x00000074, 0x00000075, 0x00000028, 0x00000029, 0x0000002A, 0x00000007,
        0x0000002B, 0x00000076, 0x0000002C, 0x00000008, 0x00000009, 0x0000002D, 0x00000077, 0x00000078,
        0x00000079, 0x0000007A, 0x0000007B, 0x00007FFE, 0x000007FC, 0x00003FFD, 0x00001FFD, 0x0FFFFFFC,
        0x000FFFE6, 0x003FFFD2, 0x000FFFE7, 0x000FFFE8, 0x003FFFD3, 0x003FFFD4, 0x003FFFD5, 0x007FFFD9,
        0x003FFFD6, 0x007FFFDA, 0x007FFFDB, 0x007FFFDC, 0x007FFFDD, 0x007FFFDE, 0x00FFFFEB, 0x007FFFDF,
        0x00FFFFEC, 0x00FFFFED, 0x003FFFD7, 0x007FFFE0, 0x00FFFFEE, 0x007FFFE1, 0x007FFFE2, 0x007FFFE3,
        0x007FFFE4, 0x001FFFDC, 0x003FFFD8, 0x007FFFE5, 0x003FFFD9, 0x007FFFE6, 0x007FFFE7, 0x00FFFFEF,
        0x003FFFDA, 0x001FFFDD, 0x000FFFE9, 0x003FFFDB, 0x003FFFDC, 0x007FFFE8, 0x007FFFE9, 0x001FFFDE,
        0x007FFFEA, 0x003FFFDD, 0x003FFFDE, 0x00FFFFF0, 0x001FFFDF, 0x003FFFDF, 0x007FFFEB, 0x007FFFEC,
        0x001FFFE0, 0x001FFFE1, 0x003FFFE0, 0x001FFFE2, 0x007FFFED, 0x003FFFE1, 0x007FFFEE, 0x007FFFEF,
        0x000FFFEA, 0x003FFFE2, 0x003FFFE3, 0x003FFFE4, 0x007FFFF0, 0x003FFFE5, 0x003FFFE6, 0x007FFFF1,
        0x03FFFFE0, 0x03FFFFE1, 0x000FFFEB, 0x0007FFF1, 0x003FFFE7, 0x007FFFF2, 0x003FFFE8, 0x01FFFFEC,
        0x03FFFFE2, 0x03FFFFE3, 0x03FFFFE4, 0x07FFFFDE, 0x07FFFFDF, 0x03FFFFE5, 0x00FFFFF1, 0x01FFFFED,
        0x0007FFF2, 0x001FFFE3, 0x03FFFFE6, 0x07FFFFE0, 0x07FFFFE1, 0x03FFFFE7, 0x07FFFFE2, 0x00FFFFF2,
        0x001FFFE4, 0x001FFFE5, 0x03FFFFE8, 0x03FFFFE9, 0x0FFFFFFD, 0x07FFFFE3, 0x07FFFFE4, 0x07FFFFE5,
        0x000FFFEC, 0x00FFFFF3, 0x000FFFED, 0x001FFFE6, 0x003FFFE9, 0x001FFFE7, 0x001FFFE8, 0x007FFFF3,
        0x003FFFEA, 0x003FFFEB, 0x01FFFFEE, 0x01FFFFEF, 0x00FFFFF4, 0x00FFFFF5, 0x03FFFFEA, 0x007FFFF4,
        0x03FFFFEB, 0x07FFFFE6, 0x03FFFFEC, 0x03FFFFED, 0x07FFFFE7, 0x07FFFFE8, 0x07FFFFE9, 0x07FFFFEA,
        0x07FFFFEB, 0x0FFFFFFE, 0x07FFFFEC, 0x07FFFFED, 0x07FFFFEE, 0x07FFFFEF, 0x07FFFFF0, 0x03FFFFEE,
        0x3FFFFFFF,
    };

    private static readonly byte[] CodeLengths =
    {
        13, 23, 28, 28, 28, 28, 28, 28,
        28, 24, 30, 28, 28, 30, 28, 28,
        28, 28, 28, 28, 28, 28, 30, 28,
        28, 28, 28, 28, 28, 28, 28, 28,
         6, 10, 10, 12, 13,  6,  8, 11,
        10, 10,  8, 11,  8,  6,  6,  6,
         5,  5,  5,  6,  6,  6,  6,  6,
         6,  6,  7,  8, 15,  6, 12, 10,
        13,  6,  7,  7,  7,  7,  7,  7,
         7,  7,  7,  7,  7,  7,  7,  7,
         7,  7,  7,  7,  7,  7,  7,  7,
         8,  7,  8, 13, 19, 13, 14,  6,
        15,  5,  6,  5,  6,  5,  6,  6,
         6,  5,  7,  7,  6,  6,  6,  5,
         6,  7,  6,  5,  5,  6,  7,  7,
         7,  7,  7, 15, 11, 14, 13, 28,
        20, 22, 20, 20, 22, 22, 22, 23,
        22, 23, 23, 23, 23, 23, 24, 23,
        24, 24, 22, 23, 24, 23, 23, 23,
        23, 21, 22, 23, 22, 23, 23, 24,
        22, 21, 20, 22, 22, 23, 23, 21,
        23, 22, 22, 24, 21, 22, 23, 23,
        21, 21, 22, 21, 23, 22, 23, 23,
        20, 22, 22, 22, 23, 22, 22, 23,
        26, 26, 20, 19, 22, 23, 22, 25,
        26, 26, 26, 27, 27, 26, 24, 25,
        19, 21, 26, 27, 27, 26, 27, 24,
        21, 21, 26, 26, 28, 27, 27, 27,
        20, 24, 20, 21, 22, 21, 21, 23,
        22, 22, 25, 25, 24, 24, 26, 23,
        26, 27, 26, 26, 27, 27, 27, 27,
        27, 28, 27, 27, 27, 27, 27, 26,
        30,
    };

    private const int EosSymbol = 256;

    // Decode map: for each bit-length present in the table, a dictionary from
    // the (right-aligned) code value to the symbol. Built once at startup.
    private static readonly Dictionary<uint, int>[] DecodeByLength = BuildDecodeMap();

    private static Dictionary<uint, int>[] BuildDecodeMap()
    {
        var maps = new Dictionary<uint, int>[31]; // lengths 1..30
        for (int i = 0; i < maps.Length; i++) maps[i] = new Dictionary<uint, int>();
        for (int sym = 0; sym < Codes.Length; sym++)
        {
            int len = CodeLengths[sym];
            maps[len].TryAdd(Codes[sym], sym);
        }
        return maps;
    }

    /// <summary>Returns the number of octets needed to Huffman-encode <paramref name="data"/>.</summary>
    public static int EncodedLength(ReadOnlySpan<byte> data)
    {
        long bits = 0;
        foreach (byte b in data) bits += CodeLengths[b];
        return (int)((bits + 7) / 8);
    }

    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length);
        ulong buffer = 0;
        int bitsInBuffer = 0;
        foreach (byte b in data)
        {
            int len = CodeLengths[b];
            buffer = (buffer << len) | Codes[b];
            bitsInBuffer += len;
            while (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                output.Add((byte)(buffer >> bitsInBuffer));
            }
        }
        if (bitsInBuffer > 0)
        {
            // Pad the final byte with the most-significant bits of the EOS code (all 1s).
            int pad = 8 - bitsInBuffer;
            buffer = (buffer << pad) | ((1UL << pad) - 1);
            output.Add((byte)buffer);
        }
        return output.ToArray();
    }

    /// <summary>Decodes a Huffman-coded octet sequence. Throws on an invalid code.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length * 2);
        uint code = 0;
        int len = 0;
        foreach (byte octet in data)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                code = (code << 1) | (uint)((octet >> bit) & 1);
                len++;
                if (len > 30) throw new InvalidDataException("HPACK Huffman: code too long");
                if (DecodeByLength[len].TryGetValue(code, out int sym))
                {
                    if (sym == EosSymbol)
                        throw new InvalidDataException("HPACK Huffman: EOS in stream");
                    output.Add((byte)sym);
                    code = 0;
                    len = 0;
                }
            }
        }
        // Any remaining bits (len > 0) must be EOS padding: at most 7 bits, all 1s.
        if (len > 7) throw new InvalidDataException("HPACK Huffman: oversized padding");
        if (len > 0)
        {
            uint expected = (1u << len) - 1;
            if (code != expected) throw new InvalidDataException("HPACK Huffman: bad padding");
        }
        return output.ToArray();
    }
}
