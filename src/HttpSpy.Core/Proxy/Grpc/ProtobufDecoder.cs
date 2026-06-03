using System.Buffers.Binary;
using System.Text;

namespace HttpSpy.Core.Proxy.Grpc;

/// <summary>Protobuf wire types (protobuf encoding spec).</summary>
public enum ProtobufWireType
{
    Varint = 0,
    Fixed64 = 1,
    LengthDelimited = 2,
    StartGroup = 3,
    EndGroup = 4,
    Fixed32 = 5,
}

/// <summary>A single decoded protobuf field (one tag/value pair).</summary>
public sealed class ProtobufField
{
    public int FieldNumber { get; init; }
    public ProtobufWireType WireType { get; init; }

    /// <summary>Raw varint / fixed value (for scalar wire types).</summary>
    public ulong NumericValue { get; init; }

    /// <summary>Bytes for length-delimited fields.</summary>
    public byte[] Bytes { get; init; } = Array.Empty<byte>();

    /// <summary>When a length-delimited field successfully parses as a nested message.</summary>
    public List<ProtobufField>? Nested { get; set; }

    /// <summary>When a length-delimited field looks like a UTF-8 string.</summary>
    public string? AsString { get; set; }
}

/// <summary>
/// A schema-less protobuf decoder. Without a <c>.proto</c> file the field names
/// are unknown, so fields are surfaced by their tag number and a best-effort
/// interpretation of the value (varint, fixed, nested message, or string).
/// This mirrors how HTTP debuggers render unknown protobuf payloads.
/// </summary>
public static class ProtobufDecoder
{
    /// <summary>Attempts to decode a protobuf message; returns null on malformed input.</summary>
    public static List<ProtobufField>? TryDecode(ReadOnlySpan<byte> data)
    {
        try { return Decode(data, depth: 0); }
        catch { return null; }
    }

    private static List<ProtobufField> Decode(ReadOnlySpan<byte> data, int depth)
    {
        if (depth > 64) throw new InvalidDataException("protobuf nesting too deep");
        var fields = new List<ProtobufField>();
        int offset = 0;
        while (offset < data.Length)
        {
            ulong tag = ReadVarint(data, ref offset);
            int fieldNumber = (int)(tag >> 3);
            var wireType = (ProtobufWireType)(int)(tag & 0x7);
            if (fieldNumber == 0) throw new InvalidDataException("protobuf field 0");

            switch (wireType)
            {
                case ProtobufWireType.Varint:
                    fields.Add(new ProtobufField
                    {
                        FieldNumber = fieldNumber, WireType = wireType,
                        NumericValue = ReadVarint(data, ref offset),
                    });
                    break;
                case ProtobufWireType.Fixed64:
                    EnsureRemaining(data, offset, 8);
                    fields.Add(new ProtobufField
                    {
                        FieldNumber = fieldNumber, WireType = wireType,
                        NumericValue = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8)),
                    });
                    offset += 8;
                    break;
                case ProtobufWireType.Fixed32:
                    EnsureRemaining(data, offset, 4);
                    fields.Add(new ProtobufField
                    {
                        FieldNumber = fieldNumber, WireType = wireType,
                        NumericValue = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)),
                    });
                    offset += 4;
                    break;
                case ProtobufWireType.LengthDelimited:
                {
                    int len = checked((int)ReadVarint(data, ref offset));
                    EnsureRemaining(data, offset, len);
                    var slice = data.Slice(offset, len);
                    offset += len;
                    var field = new ProtobufField
                    {
                        FieldNumber = fieldNumber, WireType = wireType, Bytes = slice.ToArray(),
                    };
                    // Heuristic: a printable run is almost always a string, so prefer
                    // that; otherwise try to parse the bytes as a clean nested message.
                    if (LooksLikeText(slice))
                        field.AsString = Encoding.UTF8.GetString(slice);
                    else if (len > 0 && TryDecodeNested(slice, depth, out var nested))
                        field.Nested = nested;
                    fields.Add(field);
                    break;
                }
                case ProtobufWireType.StartGroup:
                case ProtobufWireType.EndGroup:
                    // Deprecated groups: treat as malformed so nested detection stays sound.
                    throw new InvalidDataException("protobuf groups unsupported");
                default:
                    throw new InvalidDataException($"protobuf wire type {wireType}");
            }
        }
        return fields;
    }

    private static bool TryDecodeNested(ReadOnlySpan<byte> data, int depth, out List<ProtobufField> nested)
    {
        try
        {
            var parsed = Decode(data, depth + 1);
            // Only accept if it consumed everything and produced at least one field.
            if (parsed.Count > 0) { nested = parsed; return true; }
        }
        catch { /* not a nested message */ }
        nested = new List<ProtobufField>();
        return false;
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return false;
        foreach (byte b in data)
            if (b < 0x09 || (b > 0x0D && b < 0x20)) return false;
        try { _ = Encoding.UTF8.GetString(data); return true; }
        catch { return false; }
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (offset >= data.Length) throw new InvalidDataException("protobuf varint truncated");
            byte b = data[offset++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("protobuf varint too long");
        }
        return result;
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> data, int offset, int needed)
    {
        if (offset + needed > data.Length) throw new InvalidDataException("protobuf length overrun");
    }

    /// <summary>Renders a decoded message as an indented text tree for display.</summary>
    public static string Render(IEnumerable<ProtobufField> fields, int indent = 0)
    {
        var sb = new StringBuilder();
        Render(fields, sb, indent);
        return sb.ToString();
    }

    private static void Render(IEnumerable<ProtobufField> fields, StringBuilder sb, int indent)
    {
        string pad = new string(' ', indent * 2);
        foreach (var f in fields)
        {
            switch (f.WireType)
            {
                case ProtobufWireType.Varint:
                    long signed = ZigZagDecode(f.NumericValue);
                    sb.Append(pad).Append($"{f.FieldNumber}: varint {f.NumericValue}");
                    if (signed != (long)f.NumericValue) sb.Append($" (zigzag {signed})");
                    sb.AppendLine();
                    break;
                case ProtobufWireType.Fixed32:
                    sb.Append(pad).AppendLine(
                        $"{f.FieldNumber}: i32 {(uint)f.NumericValue} (float {BitConverter.Int32BitsToSingle((int)f.NumericValue):G6})");
                    break;
                case ProtobufWireType.Fixed64:
                    sb.Append(pad).AppendLine(
                        $"{f.FieldNumber}: i64 {f.NumericValue} (double {BitConverter.Int64BitsToDouble((long)f.NumericValue):G6})");
                    break;
                case ProtobufWireType.LengthDelimited:
                    if (f.Nested is not null)
                    {
                        sb.Append(pad).AppendLine($"{f.FieldNumber}: message {{");
                        Render(f.Nested, sb, indent + 1);
                        sb.Append(pad).AppendLine("}");
                    }
                    else if (f.AsString is not null)
                    {
                        sb.Append(pad).AppendLine($"{f.FieldNumber}: string \"{f.AsString}\"");
                    }
                    else
                    {
                        sb.Append(pad).AppendLine(
                            $"{f.FieldNumber}: bytes ({f.Bytes.Length}) {Convert.ToHexString(f.Bytes)}");
                    }
                    break;
            }
        }
    }

    private static long ZigZagDecode(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);
}
