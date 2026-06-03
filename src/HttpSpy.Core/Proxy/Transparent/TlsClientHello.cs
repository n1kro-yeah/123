namespace HttpSpy.Core.Proxy.Transparent;

/// <summary>
/// Minimal TLS ClientHello parser used by the transparent-capture listener to
/// recover the target host from the SNI extension (RFC 6066) without an HTTP
/// CONNECT. Only the fields needed to extract <c>server_name</c> are parsed.
/// </summary>
public static class TlsClientHello
{
    /// <summary>
    /// Extracts the SNI host name from a buffer that begins with a TLS record
    /// containing a ClientHello, or null if it cannot be found. The parser is
    /// defensive: any malformed/short input yields null rather than throwing.
    /// </summary>
    public static string? TryParseSni(ReadOnlySpan<byte> buffer)
    {
        try { return ParseSni(buffer); }
        catch { return null; }
    }

    private static string? ParseSni(ReadOnlySpan<byte> b)
    {
        int pos = 0;
        // TLS record header: type(1)=22 handshake, version(2), length(2).
        if (b.Length < 5 || b[0] != 0x16) return null;
        int recordLen = (b[3] << 8) | b[4];
        pos = 5;
        if (recordLen <= 0 || pos + recordLen > b.Length) recordLen = b.Length - pos;

        // Handshake header: type(1)=1 ClientHello, length(3).
        if (pos + 4 > b.Length || b[pos] != 0x01) return null;
        pos += 4; // skip handshake type + 3-byte length

        // client_version(2) + random(32).
        pos += 2 + 32;
        if (pos > b.Length) return null;

        // session_id.
        int sessionIdLen = ReadByte(b, ref pos);
        pos += sessionIdLen;

        // cipher_suites.
        int cipherLen = ReadUInt16(b, ref pos);
        pos += cipherLen;

        // compression_methods.
        int compLen = ReadByte(b, ref pos);
        pos += compLen;

        if (pos + 2 > b.Length) return null; // no extensions
        int extensionsLen = ReadUInt16(b, ref pos);
        int extEnd = Math.Min(pos + extensionsLen, b.Length);

        while (pos + 4 <= extEnd)
        {
            int extType = ReadUInt16(b, ref pos);
            int extLen = ReadUInt16(b, ref pos);
            if (pos + extLen > b.Length) return null;

            if (extType == 0x0000) // server_name
            {
                int listEnd = pos + extLen;
                int serverNameListLen = ReadUInt16(b, ref pos);
                int nameListEnd = Math.Min(pos + serverNameListLen, listEnd);
                while (pos + 3 <= nameListEnd)
                {
                    int nameType = ReadByte(b, ref pos);
                    int nameLen = ReadUInt16(b, ref pos);
                    if (pos + nameLen > b.Length) return null;
                    if (nameType == 0x00) // host_name
                        return System.Text.Encoding.ASCII.GetString(b.Slice(pos, nameLen));
                    pos += nameLen;
                }
                return null;
            }
            pos += extLen;
        }
        return null;
    }

    private static int ReadByte(ReadOnlySpan<byte> b, ref int pos)
    {
        if (pos + 1 > b.Length) throw new IndexOutOfRangeException();
        return b[pos++];
    }

    private static int ReadUInt16(ReadOnlySpan<byte> b, ref int pos)
    {
        if (pos + 2 > b.Length) throw new IndexOutOfRangeException();
        int v = (b[pos] << 8) | b[pos + 1];
        pos += 2;
        return v;
    }
}
