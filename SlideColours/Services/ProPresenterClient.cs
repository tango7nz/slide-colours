using System.Net.Http;
using System.Text;
using System.Text.Json;
using SlideColours.Models;

namespace SlideColours.Services;

/// <summary>
/// Connects to ProPresenter 7's local HTTP API (Preferences -> Network), streams slide-index
/// changes via the chunked endpoint, and fetches a thumbnail of each new slide.
/// </summary>
public class ProPresenterClient
{
    private readonly AppSettings _settings;
    private readonly HttpClient _stream = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private CancellationTokenSource? _connectionCts;

    public event Action<bool>? ConnectionChanged;
    public event Action<byte[]>? SlideImageReceived;
    public event Action? SlideCleared;

    /// <summary>Whether the slide-index stream is currently connected. Reflects the last <see cref="ConnectionChanged"/>.</summary>
    public bool Connected { get; private set; }

    public ProPresenterClient(AppSettings settings) => _settings = settings;

    private void SetConnected(bool connected)
    {
        Connected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    /// <summary>Drops the current connection so the next attempt picks up new host/port settings.</summary>
    public void Reconnect() => _connectionCts?.Cancel();

    public async Task RunAsync(CancellationToken appToken)
    {
        while (!appToken.IsCancellationRequested)
        {
            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
            var ct = _connectionCts.Token;
            string baseUrl = $"http://{_settings.ProPresenterHost}:{_settings.ProPresenterPort}";

            try
            {
                using var response = await _stream.GetAsync(
                    $"{baseUrl}/v1/presentation/slide_index?chunked=true",
                    HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                SetConnected(true);

                using var body = await response.Content.ReadAsStreamAsync(ct);
                var splitter = new JsonStreamSplitter();
                var buffer = new byte[16 * 1024];
                string? lastSlideKey = null;

                while (true)
                {
                    int read = await body.ReadAsync(buffer, ct);
                    if (read == 0)
                        break; // ProPresenter closed the stream

                    // If several updates arrived at once, only the newest matters
                    var documents = splitter.Push(buffer, read);
                    if (documents.Count == 0)
                        continue;
                    string doc = documents[^1];

                    using var json = JsonDocument.Parse(doc);
                    if (json.RootElement.TryGetProperty("presentation_index", out var pi)
                        && pi.ValueKind == JsonValueKind.Object)
                    {
                        int index = pi.GetProperty("index").GetInt32();
                        string uuid = pi.GetProperty("presentation_id").GetProperty("uuid").GetString() ?? "";
                        string key = $"{uuid}:{index}";
                        if (key == lastSlideKey)
                            continue;
                        lastSlideKey = key;

                        try
                        {
                            byte[] jpeg = await _http.GetByteArrayAsync(
                                $"{baseUrl}/v1/presentation/{uuid}/thumbnail/{index}?quality=200", ct);
                            SlideImageReceived?.Invoke(jpeg);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch
                        {
                            // thumbnail fetch failed — keep listening, keep the current colour
                        }
                    }
                    else
                    {
                        lastSlideKey = null;
                        SlideCleared?.Invoke();
                    }
                }
            }
            catch (OperationCanceledException) when (appToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // connection refused / dropped / bad JSON — retry below
            }

            SetConnected(false);
            try { await Task.Delay(3000, appToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Extracts complete top-level JSON documents from an arbitrary byte stream,
    /// tolerating any whitespace/newline framing between them and multi-byte UTF-8
    /// sequences split across reads.
    /// </summary>
    private sealed class JsonStreamSplitter
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _current = new();
        private readonly char[] _chars = new char[32 * 1024];
        private int _depth;
        private bool _inString, _escaped;

        public List<string> Push(byte[] bytes, int count)
        {
            var results = new List<string>();
            int charCount = _decoder.GetChars(bytes, 0, count, _chars, 0);

            for (int i = 0; i < charCount; i++)
            {
                char c = _chars[i];
                if (_depth == 0 && c != '{')
                    continue; // skip framing between documents

                _current.Append(c);

                if (_inString)
                {
                    if (_escaped) _escaped = false;
                    else if (c == '\\') _escaped = true;
                    else if (c == '"') _inString = false;
                    continue;
                }

                switch (c)
                {
                    case '"': _inString = true; break;
                    case '{': _depth++; break;
                    case '}':
                        if (--_depth == 0)
                        {
                            results.Add(_current.ToString());
                            _current.Clear();
                        }
                        break;
                }
            }
            return results;
        }
    }
}
