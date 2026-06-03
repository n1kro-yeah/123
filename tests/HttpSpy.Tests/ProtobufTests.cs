using System.Text;
using HttpSpy.Core.Proxy.Grpc;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>
/// Tests the schema-less protobuf wire decoder and the gRPC message framing
/// against hand-encoded messages (protobuf encoding spec examples).
/// </summary>
public class ProtobufTests
{
    [Fact]
    public void Decodes_Varint_Field()
    {
        // Field 1, varint 150 → 08 96 01 (the canonical protobuf example).
        var fields = ProtobufDecoder.TryDecode(new byte[] { 0x08, 0x96, 0x01 });
        Assert.NotNull(fields);
        Assert.Single(fields!);
        Assert.Equal(1, fields![0].FieldNumber);
        Assert.Equal(ProtobufWireType.Varint, fields[0].WireType);
        Assert.Equal(150ul, fields[0].NumericValue);
    }

    [Fact]
    public void Decodes_String_Field()
    {
        // Field 2, length-delimited "testing".
        var data = new byte[] { 0x12, 0x07 }.Concat(Encoding.ASCII.GetBytes("testing")).ToArray();
        var fields = ProtobufDecoder.TryDecode(data);
        Assert.NotNull(fields);
        Assert.Equal(2, fields![0].FieldNumber);
        Assert.Equal(ProtobufWireType.LengthDelimited, fields[0].WireType);
        Assert.Equal("testing", fields[0].AsString);
    }

    [Fact]
    public void Decodes_Nested_Message()
    {
        // Field 3 = message { field 1 = varint 150 }.
        var fields = ProtobufDecoder.TryDecode(new byte[] { 0x1a, 0x03, 0x08, 0x96, 0x01 });
        Assert.NotNull(fields);
        Assert.Equal(3, fields![0].FieldNumber);
        Assert.NotNull(fields[0].Nested);
        Assert.Equal(1, fields[0].Nested![0].FieldNumber);
        Assert.Equal(150ul, fields[0].Nested![0].NumericValue);
    }

    [Fact]
    public void Decodes_Fixed32_And_Fixed64()
    {
        // Field 4 fixed32 = 0x01020304, field 5 fixed64 = 0x0102030405060708.
        var data = new byte[]
        {
            0x25, 0x04, 0x03, 0x02, 0x01,
            0x29, 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
        };
        var fields = ProtobufDecoder.TryDecode(data);
        Assert.NotNull(fields);
        Assert.Equal(ProtobufWireType.Fixed32, fields![0].WireType);
        Assert.Equal(0x01020304ul, fields[0].NumericValue);
        Assert.Equal(ProtobufWireType.Fixed64, fields[1].WireType);
        Assert.Equal(0x0102030405060708ul, fields[1].NumericValue);
    }

    [Fact]
    public void Rejects_Truncated_Message()
    {
        // Length-delimited field claiming 10 bytes but only 2 present.
        Assert.Null(ProtobufDecoder.TryDecode(new byte[] { 0x12, 0x0a, 0x01, 0x02 }));
    }

    [Fact]
    public void Grpc_Decodes_Single_Uncompressed_Message()
    {
        // gRPC frame: flag=0, length=3 (BE), payload = field 1 varint 150.
        var body = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x03, 0x08, 0x96, 0x01 };
        var messages = GrpcDecoder.Decode(body);
        Assert.Single(messages);
        Assert.False(messages[0].Compressed);
        Assert.NotNull(messages[0].Decoded);
        Assert.Equal(150ul, messages[0].Decoded![0].NumericValue);
    }

    [Fact]
    public void Grpc_Decodes_Multiple_Messages()
    {
        var body = new byte[]
        {
            0x00, 0x00, 0x00, 0x00, 0x03, 0x08, 0x96, 0x01,           // message 1
            0x00, 0x00, 0x00, 0x00, 0x02, 0x10, 0x2a,                 // message 2: field 2 varint 42
        };
        var messages = GrpcDecoder.Decode(body);
        Assert.Equal(2, messages.Count);
        Assert.Equal(150ul, messages[0].Decoded![0].NumericValue);
        Assert.Equal(2, messages[1].Decoded![0].FieldNumber);
        Assert.Equal(42ul, messages[1].Decoded![0].NumericValue);
    }

    [Fact]
    public void Grpc_Detects_ContentType()
    {
        Assert.True(GrpcDecoder.IsGrpc("application/grpc"));
        Assert.True(GrpcDecoder.IsGrpc("application/grpc+proto"));
        Assert.False(GrpcDecoder.IsGrpc("application/json"));
        Assert.False(GrpcDecoder.IsGrpc(null));
    }

    [Fact]
    public void Grpc_Decodes_Real_HelloReply_StringField()
    {
        // Exact response frame captured live from grpcb.in hello.HelloService/SayHello
        // through the HttpSpy HTTP/2 MITM: HelloReply { string reply = 1 }.
        var frame = Convert.FromHexString(
            "000000001E0A1C68656C6C6F2048747470537079204D49544D20675250432074657374");
        var messages = GrpcDecoder.Decode(frame);
        Assert.Single(messages);
        var fields = messages[0].Decoded;
        Assert.NotNull(fields);
        Assert.Equal(1, fields![0].FieldNumber);
        Assert.Equal(ProtobufWireType.LengthDelimited, fields[0].WireType);
        Assert.Equal("hello HttpSpy MITM gRPC test", fields[0].AsString);
        Assert.Null(fields[0].Nested); // a printable string must NOT be mis-decoded as a nested message
        Assert.Contains("string \"hello HttpSpy MITM gRPC test\"", GrpcDecoder.RenderAll(messages));
    }

    [Fact]
    public void Grpc_Handles_Partial_Trailing_Frame()
    {
        // A complete message followed by an incomplete frame header (streaming).
        var body = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x01, 0x08, 0x00, 0x00 };
        var messages = GrpcDecoder.Decode(body);
        Assert.Single(messages); // the trailing partial frame is ignored
    }
}
