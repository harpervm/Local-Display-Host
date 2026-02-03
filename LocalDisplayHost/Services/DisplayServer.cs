using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LocalDisplayHost.Services;

/// <summary>
/// Hosts a local HTTP server (raw TCP) that serves the display page and MJPEG stream.
/// Uses TcpListener so no HTTP.SYS / URL reservation is needed — works without admin.
/// </summary>
public class DisplayServer : IDisposable
{
    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, (StreamWriter Writer, TaskCompletionSource<bool> Tcs)> _mjpegClients = new();
    private readonly Func<byte[]?> _getFrame;
    private readonly Func<Rectangle> _getStreamedBounds;

    public event Action<string>? ClientConnected;
    public event Action<string>? ClientDisconnected;

    public IReadOnlyCollection<string> ClientEndpoints =>
        _mjpegClients.Keys.ToList().AsReadOnly();

    public DisplayServer(int port, Func<byte[]?> getFrame, Func<Rectangle> getStreamedBounds)
    {
        _port = port;
        _getFrame = getFrame ?? throw new ArgumentNullException(nameof(getFrame));
        _getStreamedBounds = getStreamedBounds ?? throw new ArgumentNullException(nameof(getStreamedBounds));
    }

    public void Start()
    {
        if (_listener != null) return;

        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        _cts = null;
        foreach (var pair in _mjpegClients.Values)
        {
            try { pair.Writer.Dispose(); pair.Tcs.TrySetResult(true); } catch { }
        }
        _mjpegClients.Clear();
    }

    public void BroadcastFrame(byte[] jpegBytes)
    {
        if (jpegBytes == null || jpegBytes.Length == 0) return;

        var toRemove = new List<string>();
        foreach (var kv in _mjpegClients)
        {
            try
            {
                var sw = kv.Value.Writer;
                sw.Write("--frame\r\n");
                sw.Write("Content-Type: image/jpeg\r\n");
                sw.Write($"Content-Length: {jpegBytes.Length}\r\n\r\n");
                sw.Flush();
                // Write raw JPEG bytes to the underlying stream (StreamWriter would corrupt binary data)
                sw.BaseStream.Write(jpegBytes, 0, jpegBytes.Length);
                sw.Write("\r\n");
                sw.Flush();
            }
            catch
            {
                toRemove.Add(kv.Key);
                try { kv.Value.Tcs.TrySetResult(true); } catch { }
            }
        }
        foreach (var key in toRemove)
        {
            if (_mjpegClients.TryRemove(key, out var pair))
            {
                try { pair.Writer.Dispose(); } catch { }
                try { pair.Tcs.TrySetResult(true); } catch { }
                ClientDisconnected?.Invoke(key);
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = HandleClientAsync(client);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                if (ct.IsCancellationRequested) break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        await using var stream = client.GetStream();
        try
        {
            var (method, path, body) = await ReadRequestAsync(stream).ConfigureAwait(false);
            if (path == null)
            {
                await SendResponseAsync(stream, 400, "text/plain", "Bad Request").ConfigureAwait(false);
                return;
            }

            if (path == "/input")
            {
                if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    await SendResponseAsync(stream, 200, "text/plain", "", customHeaders: "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type\r\n").ConfigureAwait(false);
                    return;
                }
                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    HandleInput(body);
                    await SendResponseAsync(stream, 200, "application/json", "{}").ConfigureAwait(false);
                    return;
                }
            }

            if (path == "" || path == "/" || path == "/display" || path == "/index.html")
            {
                var html = GetDisplayPageHtml();
                var bytes = Encoding.UTF8.GetBytes(html);
                await SendResponseAsync(stream, 200, "text/html; charset=utf-8", null, bytes).ConfigureAwait(false);
                return;
            }

            if (path == "/stream")
            {
                await ServeMjpegStreamAsync(stream, endpoint).ConfigureAwait(false);
                return;
            }

            await SendResponseAsync(stream, 404, "text/plain", "Not Found").ConfigureAwait(false);
        }
        finally
        {
            try { client.Dispose(); } catch { }
        }
    }

    private void HandleInput(byte[]? body)
    {
        if (body == null || body.Length == 0) return;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            var bounds = _getStreamedBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0)
                bounds = ScreenCapture.GetStreamedBounds(0); // fallback to primary

            if (type == "move" && root.TryGetProperty("x", out var xProp) && root.TryGetProperty("y", out var yProp))
            {
                var x = xProp.GetDouble();
                var y = yProp.GetDouble();
                InputInjector.MouseMoveNormalized(bounds, x, y);
                return;
            }
            if (type == "click" && root.TryGetProperty("x", out var cx) && root.TryGetProperty("y", out var cy) && root.TryGetProperty("button", out var btn) && root.TryGetProperty("down", out var downProp))
            {
                var nx = cx.GetDouble();
                var ny = cy.GetDouble();
                var button = btn.GetInt32();
                var down = downProp.GetBoolean();
                InputInjector.MouseMoveNormalized(bounds, nx, ny);
                InputInjector.MouseButton(button, down);
                return;
            }
            if (type == "key" && root.TryGetProperty("keyCode", out var kc) && root.TryGetProperty("down", out var kDownProp))
            {
                var keyCode = (ushort)kc.GetInt32();
                var down = kDownProp.GetBoolean();
                InputInjector.Key(keyCode, down);
            }
        }
        catch { /* ignore malformed input */ }
    }

    private static async Task<(string method, string? path, byte[]? body)> ReadRequestAsync(Stream stream)
    {
        var buffer = new byte[8192];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read)).ConfigureAwait(false);
            if (n <= 0) return ("", null, null);
            read += n;
            var text = Encoding.ASCII.GetString(buffer.AsSpan(0, read));
            var firstLineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (firstLineEnd < 0) continue;

            var firstLine = text.AsSpan(0, firstLineEnd);
            var methodEnd = firstLine.IndexOf(' ');
            if (methodEnd < 0) return ("", null, null);
            var method = firstLine[..methodEnd].ToString();
            var pathStart = methodEnd + 1;
            var pathEndInSlice = firstLine.Slice(pathStart).IndexOf(' ');
            var path = pathEndInSlice < 0
                ? firstLine.Slice(pathStart).ToString()
                : firstLine.Slice(pathStart, pathEndInSlice).ToString(); // pathEndInSlice is length of path in slice
            var q = path.IndexOf('?');
            if (q >= 0) path = path[..q];
            path = path.TrimEnd('/'); // normalize /display/ to /display

            var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0) continue;
            var headerSection = text.AsSpan(0, headerEnd);
            var contentLength = 0;
            var clIdx = headerSection.IndexOf("Content-Length:", StringComparison.OrdinalIgnoreCase);
            if (clIdx >= 0)
            {
                var rest = headerSection.Slice(clIdx + 15).Trim();
                var end = 0;
                while (end < rest.Length && char.IsDigit(rest[end])) end++;
                if (end > 0) int.TryParse(rest[..end].ToString(), out contentLength);
            }

            byte[]? body = null;
            var bodyStart = headerEnd + 4;
            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && contentLength > 0 && contentLength <= 4096)
            {
                var totalNeeded = bodyStart + contentLength;
                while (read < totalNeeded && read < buffer.Length)
                {
                    n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read)).ConfigureAwait(false);
                    if (n <= 0) break;
                    read += n;
                }
                if (read >= bodyStart + contentLength)
                    body = buffer.AsSpan(bodyStart, contentLength).ToArray();
            }
            return (method, path, body);
        }
        return ("", null, null);
    }

    private static async Task SendResponseAsync(Stream stream, int statusCode, string contentType, string? body, byte[]? bodyBytes = null, string? customHeaders = null)
    {
        var status = statusCode switch { 200 => "OK", 404 => "Not Found", 400 => "Bad Request", _ => "Error" };
        var content = bodyBytes ?? (body != null ? Encoding.UTF8.GetBytes(body) : Array.Empty<byte>());
        var extra = string.IsNullOrEmpty(customHeaders) ? "" : customHeaders.TrimEnd('\r', '\n') + "\r\n";
        var headers = $"HTTP/1.1 {statusCode} {status}\r\nContent-Type: {contentType}\r\nContent-Length: {content.Length}\r\nConnection: close\r\n{extra}\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes).ConfigureAwait(false);
        if (content.Length > 0)
            await stream.WriteAsync(content).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private async Task ServeMjpegStreamAsync(Stream stream, string endpoint)
    {
        var headers = "HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=frame\r\nConnection: keep-alive\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);

        var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
        var tcs = new TaskCompletionSource<bool>();
        _mjpegClients[endpoint] = (writer, tcs);
        ClientConnected?.Invoke(endpoint);

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _mjpegClients.TryRemove(endpoint, out _);
            ClientDisconnected?.Invoke(endpoint);
            try { await writer.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private static string GetDisplayPageHtml()
    {
        return """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Local Display Host – Extended display</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            background: #0d0d0d;
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
        }
        .container {
            width: 100%;
            max-width: 100%;
            height: 100vh;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
        }
        #stream {
            max-width: 100%;
            max-height: 100%;
            object-fit: contain;
            background: #000;
            cursor: none;
        }
        .status {
            position: fixed;
            top: 12px;
            left: 50%;
            transform: translateX(-50%);
            padding: 8px 16px;
            background: rgba(0,0,0,0.7);
            color: #aaa;
            font-size: 12px;
            border-radius: 6px;
            z-index: 10;
        }
        .status.connected { color: #4ade80; }
        .status.error { color: #f87171; }
    </style>
</head>
<body>
    <div class="status" id="status">Connecting...</div>
    <div class="container">
        <img id="stream" src="/stream" alt="Display stream" tabindex="0" />
    </div>
    <script>
        const img = document.getElementById('stream');
        const status = document.getElementById('status');
        img.onload = () => { status.textContent = 'Connected – click on the image to focus, then use mouse and keyboard'; status.className = 'status connected'; };
        img.onerror = () => { status.textContent = 'Stream error'; status.className = 'status error'; };

        function norm(x, y) {
            const rect = img.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) return { x: 0.5, y: 0.5 };
            const xNorm = (x - rect.left) / rect.width;
            const yNorm = (y - rect.top) / rect.height;
            return { x: Math.max(0, Math.min(1, xNorm)), y: Math.max(0, Math.min(1, yNorm)) };
        }
        function send(type, data) {
            fetch('/input', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ type, ...data }) }).catch(() => {});
        }
        var lastMove = 0;
        img.addEventListener('mousemove', e => {
            const now = Date.now();
            if (now - lastMove < 30) return;
            lastMove = now;
            const n = norm(e.clientX, e.clientY);
            send('move', { x: n.x, y: n.y });
        });
        img.addEventListener('mousedown', e => {
            img.focus();
            const n = norm(e.clientX, e.clientY);
            send('click', { x: n.x, y: n.y, button: e.button, down: true });
            e.preventDefault();
        });
        img.addEventListener('mouseup', e => {
            const n = norm(e.clientX, e.clientY);
            send('click', { x: n.x, y: n.y, button: e.button, down: false });
            e.preventDefault();
        });
        img.addEventListener('contextmenu', e => e.preventDefault());
        img.addEventListener('keydown', e => { send('key', { keyCode: e.keyCode, down: true }); e.preventDefault(); });
        img.addEventListener('keyup', e => { send('key', { keyCode: e.keyCode, down: false }); e.preventDefault(); });
    </script>
</body>
</html>
""";
    }

    public void Dispose() => Stop();
}
