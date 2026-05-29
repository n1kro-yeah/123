# HttpSpy

A Windows desktop HTTP/HTTPS debugger — a feature-parity analog of HTTP Debugger.

HttpSpy captures, decrypts, inspects, modifies and replays HTTP/HTTPS traffic
through a built-in man-in-the-middle proxy. It is written in **C# / .NET 8** with
an **Avalonia** UI, so the exact same codebase builds a native Windows `.exe`
and can be developed and demoed on Linux/macOS.

![HttpSpy capture grid](docs/capture.png)

## Features

- **Live capture** of HTTP and HTTPS traffic with a real-time session grid
  (index, protocol, method, result, host, URL, content-type, size, time, process).
- **HTTPS decryption** via an auto-generated root CA and per-host leaf
  certificates (issued on the fly, cached under `%APPDATA%\HttpSpy`).
- **System proxy integration** on Windows — capture starts intercepting
  immediately (WinINET registry + `InternetSetOption` refresh). On other
  platforms, point your client at `127.0.0.1:8888`.
- **Process attribution** on Windows via the IP Helper API (`GetExtendedTcpTable`).
- **Inspectors:** request/response headers, query string, cookies, form fields,
  body (with JSON/XML pretty-printing), raw view, hex dump, image preview, and
  per-phase timings.
- **Protocols:** HTTP/1.1 (full), WebSocket frame capture (RFC 6455),
  Server-Sent Events (text/event-stream). HTTPS via MITM.
- **Submitter** (request builder): compose any request or clone a captured one,
  edit method/URL/headers/body, and resend.
- **Rules engine:** Block, Redirect, Auto-Reply (mock responses), Breakpoints
  (pause + edit request/response live), Modify request/response, Delay, and
  Highlight — matched by Contains / Wildcard / Regex / Exact.
- **Dashboard:** status-code distribution, top hosts, content types and method
  breakdown, plus totals (requests, bytes, average time, errors, HTTPS count).
- **Code generation** from any session: cURL, C#, Python, JavaScript.
- **Export / persistence:** HAR 1.2, CSV (Excel), and a compressed `.hspy`
  session file (save/restore full captures including bodies).

## Project layout

```
HttpSpy.sln
src/
  HttpSpy.Core/    # capture engine, models, rules, exporters (no UI, net8.0 library)
  HttpSpy.App/     # Avalonia desktop UI (WinExe, net8.0)
```

`HttpSpy.Core` has no UI dependency and can be referenced from tests or other
front-ends. `HttpSpy.App` is the desktop client.

## Build & run on Windows (target deployment: `C:\HttpSpy`)

Prerequisites: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
# from the repository root
dotnet restore
dotnet build -c Release

# run directly
dotnet run -c Release --project src\HttpSpy.App

# …or publish a self-contained single-file exe into C:\HttpSpy
dotnet publish src\HttpSpy.App -c Release -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -o C:\HttpSpy
# then run C:\HttpSpy\HttpSpy.exe
```

### First run

1. Launch HttpSpy and click **Trust cert** (HTTPS menu → *Install / trust root
   certificate*). This adds the HttpSpy root CA to your *Current User → Trusted
   Root* store so HTTPS can be decrypted. The certificate is also exported to
   `%APPDATA%\HttpSpy\Certificates\HttpSpyRootCA.cer` for manual import.
2. Click **Start capture**. On Windows the system proxy is configured
   automatically; browser/app traffic appears live in the grid.
3. Click **Stop capture** to restore the previous proxy settings.

## Build & run on Linux/macOS (development)

```bash
dotnet build -c Release
dotnet run -c Release --project src/HttpSpy.App
```

The system-proxy and trust-store automation are Windows-only; on Linux/macOS
configure your client to use `http://127.0.0.1:8888` and import the exported
root certificate (`~/.config/HttpSpy/Certificates/HttpSpyRootCA.cer`) manually.

## How HTTPS decryption works

HttpSpy is a MITM debugging proxy (the same approach used by Fiddler, Charles
and mitmproxy). For each `CONNECT host:443` it issues a leaf certificate signed
by the local HttpSpy root CA, terminates TLS with the client, and opens its own
TLS connection to the origin — so it can read and rewrite the plaintext while
both legs stay encrypted on the wire. Decryption only works for clients that
trust the HttpSpy root CA, which is why the **Trust cert** step is required.

> HTTP Debugger's "no proxy configuration" capture relies on a signed
> kernel-mode WFP driver, which cannot be reproduced in managed code. HttpSpy
> achieves the same end result (decrypted HTTPS, full inspection, modification
> and replay) with a user-mode MITM proxy and automatic system-proxy setup.

## Listening address

Default `127.0.0.1:8888` (configurable from the toolbar). Chain to an upstream
proxy via `ProxyOptions.UpstreamProxyHost/Port`.
