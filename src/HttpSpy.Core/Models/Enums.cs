namespace HttpSpy.Core.Models;

/// <summary>Lifecycle state of a captured HTTP transaction.</summary>
public enum SessionState
{
    Pending,
    RequestReceived,
    SentToServer,
    ReceivingResponse,
    Completed,
    Aborted,
    Faulted
}

/// <summary>High level classification used for grid coloring and filtering.</summary>
public enum SessionKind
{
    Http,
    Https,
    WebSocket,
    ServerSentEvents,
    Connect,
    Tunnel
}

/// <summary>Direction of a streamed message (WebSocket frame / SSE event).</summary>
public enum MessageDirection
{
    ClientToServer,
    ServerToClient
}

/// <summary>WebSocket frame opcodes (RFC 6455).</summary>
public enum WebSocketOpcode
{
    Continuation = 0x0,
    Text = 0x1,
    Binary = 0x2,
    Close = 0x8,
    Ping = 0x9,
    Pong = 0xA
}

/// <summary>The category a captured body falls into, used to pick the right viewer.</summary>
public enum BodyContentType
{
    None,
    Text,
    Json,
    Xml,
    Html,
    Css,
    JavaScript,
    Image,
    Font,
    Binary,
    Form,
    Multipart
}

/// <summary>What a rule should do when it matches.</summary>
public enum RuleAction
{
    None,
    Block,
    Redirect,
    Breakpoint,
    AutoReply,
    ModifyRequest,
    ModifyResponse,
    Delay,
    Highlight,
    /// <summary>Serve a local file as the response body (Map Local).</summary>
    MapLocal,
    /// <summary>Transparently reroute the upstream TCP endpoint (TCP/IP Redirector analog).</summary>
    RedirectEndpoint,
    /// <summary>Auto-bookmark matching transactions (Conditional Bookmarks analog).</summary>
    Bookmark
}

/// <summary>Which phase of a transaction a breakpoint pauses on.</summary>
public enum BreakpointPhase
{
    BeforeRequest,
    BeforeResponse,
    Both
}

/// <summary>Which part of a transaction a regex HTTP-modifier rule rewrites.</summary>
public enum ModifierTarget
{
    RequestHeaders,
    RequestBody,
    ResponseHeaders,
    ResponseBody
}

/// <summary>The grid column a Standard highlighting rule is evaluated against.</summary>
public enum HighlightColumn
{
    Url,
    Host,
    Method,
    Status,
    ContentType,
    Process,
    RequestSize,
    ResponseSize,
    Duration,
    Speed
}

/// <summary>Comparison operator for a Standard highlighting rule.</summary>
public enum HighlightOperator
{
    Contains,
    IsSame,
    StartsWith,
    EndsWith,
    IsEqual,
    IsLess,
    IsBigger,
    IsBetween
}
