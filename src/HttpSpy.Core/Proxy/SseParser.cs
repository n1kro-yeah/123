using System.Text;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Proxy;

/// <summary>
/// Incrementally parses a stream of Server-Sent Events (text/event-stream).
/// Fed with raw byte chunks as they arrive; emits <see cref="ServerSentEvent"/>
/// records on each blank-line delimiter.
/// </summary>
public sealed class SseParser
{
    private readonly StringBuilder _lineBuffer = new();
    private string? _event;
    private readonly StringBuilder _data = new();
    private string? _lastId;
    private int? _retry;
    private int _index;

    public IEnumerable<ServerSentEvent> Feed(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            char c = (char)bytes[i];
            if (c == '\n')
            {
                var line = _lineBuffer.ToString();
                _lineBuffer.Clear();
                if (line.EndsWith('\r')) line = line[..^1];

                if (line.Length == 0)
                {
                    if (_data.Length > 0)
                    {
                        var evt = new ServerSentEvent
                        {
                            Index = ++_index,
                            EventName = _event,
                            Data = _data.ToString().TrimEnd('\n'),
                            Id = _lastId,
                            RetryMs = _retry,
                        };
                        _event = null;
                        _data.Clear();
                        _retry = null;
                        yield return evt;
                    }
                }
                else
                {
                    ParseField(line);
                }
            }
            else
            {
                _lineBuffer.Append(c);
            }
        }
    }

    private void ParseField(string line)
    {
        if (line.StartsWith(':')) return; // comment

        int colon = line.IndexOf(':');
        string field, value;
        if (colon < 0)
        {
            field = line;
            value = string.Empty;
        }
        else
        {
            field = line[..colon];
            value = line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
        }

        switch (field)
        {
            case "event": _event = value; break;
            case "data": _data.Append(value).Append('\n'); break;
            case "id": _lastId = value; break;
            case "retry": if (int.TryParse(value, out int r)) _retry = r; break;
        }
    }
}
