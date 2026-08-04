using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SlideColours.Models;

namespace SlideColours.Services;

/// <summary>
/// A tiny HTTP listener so Bitfocus Companion (Stream Deck) can recall scenes and toggle output.
///
/// Deliberately built on <see cref="TcpListener"/> rather than <c>HttpListener</c>: HttpListener
/// needs an admin-registered URL ACL to accept anything but localhost, which would be a miserable
/// requirement on a locked-down church booth PC. A socket bind needs no such thing, and the subset
/// of HTTP/1.1 that Companion's Generic HTTP module speaks is small enough to serve by hand.
/// </summary>
public sealed class RemoteControlServer : IDisposable
{
    /// <summary>Cap on the request head we'll buffer, so a hostile or broken client can't grow it.</summary>
    private const int MaxRequestBytes = 8 * 1024;

    private readonly AppSettings _settings;
    private readonly LightingEngine _engine;
    private readonly object _sync = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string? _status;

    /// <summary>Fired when the listener starts, stops or fails; null means "running fine".</summary>
    public event Action<string?>? StatusChanged;

    /// <summary>Last problem, or null when healthy (or deliberately switched off).</summary>
    public string? Status => _status;

    public bool Running
    {
        get { lock (_sync) return _listener != null; }
    }

    public RemoteControlServer(AppSettings settings, LightingEngine engine)
    {
        _settings = settings;
        _engine = engine;
    }

    /// <summary>Starts or stops the listener to match the current settings. Safe to call repeatedly.</summary>
    public void Restart()
    {
        Stop();
        if (!_settings.RemoteEnabled)
        {
            Report(null);
            return;
        }

        lock (_sync)
        {
            try
            {
                int port = Math.Clamp(_settings.RemotePort, 1, 65535);
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                _listener = listener;
                _cts = new CancellationTokenSource();
                _ = AcceptLoopAsync(listener, _cts.Token);
                Report(null);
            }
            catch (Exception ex)
            {
                _listener = null;
                // Nearly always "port already in use" — say so rather than failing silently.
                Report($"Remote control couldn't start on port {_settings.RemotePort}: {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            try { _listener?.Stop(); } catch { /* already down */ }
            _listener = null;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break; // listener stopped underneath us
            }
            catch (Exception ex)
            {
                Report($"Remote control stopped accepting connections: {ex.Message}");
                break;
            }

            // One request per connection, handled off the accept loop so a slow client can't
            // block the next button press.
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 5000;

                string? head = await ReadHeadAsync(stream);
                if (head == null)
                    return;

                await RouteAsync(stream, head);
            }
            catch
            {
                // A dropped or malformed connection must never take the server down.
            }
        }
    }

    /// <summary>Reads up to the blank line that ends the request head. Null if it never arrives.</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[1024];
        var head = new StringBuilder();
        while (head.Length < MaxRequestBytes)
        {
            int read = await stream.ReadAsync(buffer);
            if (read <= 0)
                return null;

            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (head.ToString().Contains("\r\n\r\n") || head.ToString().Contains("\n\n"))
                return head.ToString();
        }
        return null;
    }

    private async Task RouteAsync(NetworkStream stream, string head)
    {
        string[] lines = head.Split('\n');
        string[] request = lines[0].Trim().Split(' ');
        if (request.Length < 2)
        {
            await WriteAsync(stream, 400, "text/plain", "Bad request");
            return;
        }

        string target = request[1];
        string path = target.Split('?')[0];
        string query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";

        if (!IsAuthorised(query, lines))
        {
            // Say who we are, so a wrong key is obviously different from a wrong address.
            await WriteAsync(stream, 401, "application/json",
                Json(new { error = "unauthorised", hint = "add ?key=... from Settings → Remote control" }));
            return;
        }

        string[] segments = path.Trim('/').Split('/')
            .Select(Uri.UnescapeDataString)
            .Where(s => s.Length > 0)
            .ToArray();

        try
        {
            await DispatchAsync(stream, segments);
        }
        catch (Exception ex)
        {
            await WriteAsync(stream, 500, "application/json", Json(new { error = ex.Message }));
        }
    }

    private async Task DispatchAsync(NetworkStream stream, string[] segments)
    {
        // "/" — a human landing page, so browsing to the address confirms it's reachable and
        // shows the URLs to paste into Companion.
        if (segments.Length == 0)
        {
            await WriteAsync(stream, 200, "text/html; charset=utf-8", HelpPage());
            return;
        }

        switch (segments[0].ToLowerInvariant())
        {
            case "status":
            case "scenes":
                await WriteAsync(stream, 200, "application/json", Json(StateSnapshot()));
                return;

            case "scene" when segments.Length >= 2:
            {
                string wanted = segments[1];
                var scene = _settings.Scenes.ToList()
                    .FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase));
                if (scene == null)
                {
                    await WriteAsync(stream, 404, "application/json", Json(new
                    {
                        error = $"No scene called \"{wanted}\"",
                        scenes = _settings.Scenes.Select(s => s.Name).ToArray(),
                    }));
                    return;
                }

                _engine.ApplyScene(scene);
                await WriteAsync(stream, 200, "application/json", Json(StateSnapshot()));
                return;
            }

            case "output" when segments.Length >= 2:
            {
                string action = segments[1].ToLowerInvariant();
                _engine.Enabled = action switch
                {
                    "on" or "1" or "true" => true,
                    "off" or "0" or "false" => false,
                    "toggle" => !_engine.Enabled,
                    _ => throw new ArgumentException("Use /output/on, /output/off or /output/toggle"),
                };
                await WriteAsync(stream, 200, "application/json", Json(StateSnapshot()));
                return;
            }

            default:
                await WriteAsync(stream, 404, "application/json",
                    Json(new { error = "Unknown command", see = "/" }));
                return;
        }
    }

    /// <summary>Everything Companion might want to read back, in one object.</summary>
    private object StateSnapshot()
    {
        var scenes = _settings.Scenes.ToList();
        return new
        {
            output = _engine.Enabled,
            active = _engine.ActiveScene()?.Name,
            scenes = scenes.Select(s => s.Name).ToArray(),
        };
    }

    private bool IsAuthorised(string query, string[] headerLines)
    {
        string expected = _settings.RemoteKey ?? "";
        if (expected.Length == 0)
            return true; // no key configured — the user has opted out of the check

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = pair.Split('=', 2);
            if (bits.Length == 2 && bits[0].Equals("key", StringComparison.OrdinalIgnoreCase)
                                 && Uri.UnescapeDataString(bits[1]) == expected)
            {
                return true;
            }
        }

        foreach (var line in headerLines)
        {
            if (line.StartsWith("X-Api-Key:", StringComparison.OrdinalIgnoreCase)
                && line["X-Api-Key:".Length..].Trim() == expected)
            {
                return true;
            }
        }
        return false;
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static async Task WriteAsync(NetworkStream stream, int status, string contentType, string body)
    {
        string reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            _ => "Internal Server Error",
        };
        byte[] payload = Encoding.UTF8.GetBytes(body);
        string head = $"HTTP/1.1 {status} {reason}\r\n"
                    + $"Content-Type: {contentType}\r\n"
                    + $"Content-Length: {payload.Length}\r\n"
                    + "Cache-Control: no-store\r\n"
                    + "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private string HelpPage()
    {
        string key = _settings.RemoteKey.Length > 0 ? $"?key={Uri.EscapeDataString(_settings.RemoteKey)}" : "";
        string host = $"{LocalAddress()}:{_settings.RemotePort}";
        var rows = new StringBuilder();
        foreach (var scene in _settings.Scenes.ToList())
        {
            string url = $"http://{host}/scene/{Uri.EscapeDataString(scene.Name)}{key}";
            rows.Append($"<tr><td>Recall &ldquo;{WebEscape(scene.DisplayName)}&rdquo;</td><td><code>{WebEscape(url)}</code></td></tr>");
        }
        if (_settings.Scenes.Count == 0)
            rows.Append("<tr><td colspan=\"2\"><em>No scenes yet — create them in Settings → Scenes.</em></td></tr>");

        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>Slide Colours remote control</title>
            <style>
              body{font-family:Segoe UI,system-ui,sans-serif;margin:2rem;line-height:1.5;color:#222}
              code{background:#f2f2f5;padding:.15rem .35rem;border-radius:4px;font-size:.9em}
              table{border-collapse:collapse;margin:1rem 0}
              td{border-bottom:1px solid #e3e3e8;padding:.4rem .8rem .4rem 0;vertical-align:top}
            </style></head><body>
            <h1>Slide Colours</h1>
            <p>Remote control is running. Paste these into a Bitfocus Companion
               <strong>Generic: HTTP Requests</strong> action (method <code>GET</code>).</p>
            <table>{{rows}}
              <tr><td>Output on</td><td><code>http://{{host}}/output/on{{key}}</code></td></tr>
              <tr><td>Output off</td><td><code>http://{{host}}/output/off{{key}}</code></td></tr>
              <tr><td>Output toggle</td><td><code>http://{{host}}/output/toggle{{key}}</code></td></tr>
              <tr><td>Current state (JSON)</td><td><code>http://{{host}}/status{{key}}</code></td></tr>
            </table>
            <p>Scene names are matched ignoring case. Rename a scene and its URL changes with it.</p>
            </body></html>
            """;
    }

    private static string WebEscape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Best guess at this machine's LAN address, for showing the URL to type into Companion.</summary>
    public static string LocalAddress()
    {
        try
        {
            // No traffic is sent; connecting a UDP socket just picks the route the OS would use.
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect("8.8.8.8", 65530);
            if (probe.LocalEndPoint is IPEndPoint local)
                return local.Address.ToString();
        }
        catch
        {
            // offline or no route — fall through
        }
        return "localhost";
    }

    private void Report(string? status)
    {
        if (_status == status)
            return;
        _status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose() => Stop();
}
