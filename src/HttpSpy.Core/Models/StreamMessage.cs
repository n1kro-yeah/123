namespace HttpSpy.Core.Models;

/// <summary>A single WebSocket frame captured on an upgraded connection.</summary>
public sealed class WebSocketFrame
{
    public int Index { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public MessageDirection Direction { get; set; }
    public WebSocketOpcode Opcode { get; set; }
    public bool Fin { get; set; } = true;
    public bool Masked { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    /// <summary>True when the frame's payload was larger than the capture cap and only a prefix is retained.</summary>
    public bool Truncated { get; set; }

    /// <summary>The full declared payload length on the wire (may exceed <see cref="Length"/> when truncated).</summary>
    public long DeclaredLength { get; set; }

    public long Length => Payload.LongLength;

    public bool IsText => Opcode == WebSocketOpcode.Text;

    public string TextPayload =>
        IsText ? System.Text.Encoding.UTF8.GetString(Payload) : $"<{Payload.Length} bytes>";

    public string Summary
    {
        get
        {
            string arrow = Direction == MessageDirection.ClientToServer ? "▲ out" : "▼ in";
            string body = Opcode switch
            {
                WebSocketOpcode.Text => Truncate(TextPayload, 80),
                WebSocketOpcode.Close => "[close]",
                WebSocketOpcode.Ping => "[ping]",
                WebSocketOpcode.Pong => "[pong]",
                _ => $"[{Opcode} {Payload.Length}b]"
            };
            string suffix = Truncated ? $"  (truncated, {DeclaredLength:N0}b on wire)" : string.Empty;
            return $"{arrow}  {body}{suffix}";
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

/// <summary>A single Server-Sent-Event record parsed from a text/event-stream body.</summary>
public sealed class ServerSentEvent
{
    public int Index { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string? Id { get; set; }
    public string? EventName { get; set; }
    public string Data { get; set; } = string.Empty;
    public int? RetryMs { get; set; }

    public string Summary => $"{EventName ?? "message"}: {(Data.Length > 100 ? Data[..100] + "…" : Data)}";
}
