using System.Diagnostics;
using SlideColours.Models;
using WpfColor = System.Windows.Media.Color;

namespace SlideColours.Services;

/// <summary>
/// Watches a display and continuously reports the colour to put on stage — the "TV backlight"
/// source, for slides whose background is animated.
///
/// Frame-by-frame the dominant-hue extraction is jumpy: a gradient drifting across a hue-bin
/// boundary would snap the lights from one hue to the next. So the colour is smoothed here with
/// an exponential moving average in HSV (interpolating hue the short way round the wheel), and
/// the engine then applies only a short fade on top.
/// </summary>
public class ScreenColourSource : IDisposable
{
    /// <summary>How long to wait before rebuilding a duplication that failed or was lost.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly AppSettings _settings;
    private readonly object _sync = new();
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _active;
    private volatile bool _disposed;
    private volatile bool _restart;
    private Thread? _thread;

    private bool _hasAverage;
    private double _avgH, _avgS, _avgV;
    private string? _status;

    /// <summary>Fired from the capture thread with the smoothed colour, or null for a colourless screen.</summary>
    public event Action<WpfColor?>? ColourSampled;

    /// <summary>Fired when capture breaks or recovers; null = healthy. Also fired when stopping.</summary>
    public event Action<string?>? StatusChanged;

    /// <summary>Last reported problem, or null when healthy.</summary>
    public string? Status => _status;

    public ScreenColourSource(AppSettings settings) => _settings = settings;

    /// <summary>Starts or stops capturing. Cheap to call repeatedly with the same value.</summary>
    public void SetActive(bool active)
    {
        if (_active == active || _disposed)
            return;

        _active = active;
        if (active)
        {
            // Snap to the first frame rather than drifting in from the colour we left behind.
            _hasAverage = false;
            lock (_sync)
            {
                if (_thread == null)
                {
                    // A dedicated thread, not the pool: this blocks in AcquireNextFrame for its whole life.
                    _thread = new Thread(Loop) { IsBackground = true, Name = "Screen capture" };
                    _thread.Start();
                }
            }
        }
        else
        {
            Report(null);
        }
        _wake.Set();
    }

    /// <summary>Call after the capture settings change so the next frame uses the new display.</summary>
    public void Restart()
    {
        _restart = true;
        _wake.Set();
    }

    private void Loop()
    {
        DesktopDuplicator? duplicator = null;
        var clock = Stopwatch.StartNew();
        var lastSample = TimeSpan.Zero;
        var lastFailure = TimeSpan.MinValue;

        try
        {
            while (!_disposed)
            {
                // Nothing here may throw out of the loop: this thread is the only thing keeping
                // capture alive, so an unexpected failure has to back off and retry, not exit.
                try
                {
                    if (!_active)
                    {
                        duplicator?.Dispose();
                        duplicator = null;
                        _hasAverage = false;
                        _wake.WaitOne(1000);
                        continue;
                    }

                    if (_restart)
                    {
                        _restart = false;
                        duplicator?.Dispose();
                        duplicator = null;
                        _hasAverage = false;
                    }

                    if (duplicator == null)
                    {
                        // Back off after a failure so a disconnected projector doesn't spin the CPU.
                        if (lastFailure != TimeSpan.MinValue && clock.Elapsed - lastFailure < RetryDelay)
                        {
                            _wake.WaitOne(250);
                            continue;
                        }

                        try
                        {
                            duplicator = new DesktopDuplicator(_settings.CaptureDisplay);
                            Report(null);
                        }
                        catch (CaptureException ex)
                        {
                            lastFailure = clock.Elapsed;
                            Report(ex.Message);
                            continue;
                        }
                    }

                    int fps = Math.Clamp(_settings.CaptureFps, 1, 120);
                    var interval = TimeSpan.FromMilliseconds(1000.0 / fps);

                    switch (duplicator.Capture(200))
                    {
                        case CaptureResult.Updated:
                            var now = clock.Elapsed;
                            Sample(duplicator, (now - lastSample).TotalMilliseconds);
                            lastSample = now;

                            // Hold off the next capture instead of dropping frames. Duplication
                            // accumulates changes while we wait, so the next call still returns the
                            // newest picture — we just look at it fewer times per second.
                            var remaining = interval - (clock.Elapsed - now);
                            if (remaining > TimeSpan.Zero)
                                _wake.WaitOne(remaining);
                            break;

                        case CaptureResult.Lost:
                            // Resolution change, GPU reset, a full-screen app taking over, session
                            // lock. Perfectly normal — drop the duplication and rebuild it.
                            duplicator.Dispose();
                            duplicator = null;
                            lastFailure = clock.Elapsed;
                            break;

                        case CaptureResult.NoChange:
                        case CaptureResult.Skipped:
                            // Nothing new on screen — hold the current colour.
                            break;
                    }
                }
                catch (Exception ex)
                {
                    duplicator?.Dispose();
                    duplicator = null;
                    lastFailure = clock.Elapsed;
                    Report($"Screen capture failed: {ex.Message}");
                }
            }
        }
        finally
        {
            duplicator?.Dispose();
            lock (_sync)
            {
                _thread = null;
            }
        }
    }

    private void Sample(DesktopDuplicator duplicator, double elapsedMs)
    {
        var colour = ColorExtractor.Extract(
            duplicator.Pixels, duplicator.PixelWidth, duplicator.PixelHeight, duplicator.PixelWidth * 4);

        if (colour is not { } raw)
        {
            // A black or plain-white screen carries no hue; let the engine's fallback rule decide.
            ColourSampled?.Invoke(null);
            return;
        }

        ColorExtractor.RgbToHsv(raw.R, raw.G, raw.B, out double h, out double s, out double v);

        if (!_hasAverage)
        {
            // First frame after starting: snap, don't drift in from a stale colour.
            _avgH = h;
            _avgS = s;
            _avgV = v;
            _hasAverage = true;
        }
        else
        {
            double tau = Math.Max(1, _settings.CaptureSmoothingMs);
            double alpha = 1 - Math.Exp(-Math.Clamp(elapsedMs, 1, 1000) / tau);

            // Hue is circular: 350° -> 10° must travel +20°, not -340°.
            double delta = ((h - _avgH + 540) % 360) - 180;
            _avgH = (_avgH + alpha * delta + 360) % 360;
            _avgS += alpha * (s - _avgS);
            _avgV += alpha * (v - _avgV);
        }

        ColourSampled?.Invoke(ColorExtractor.HsvToRgb(_avgH, _avgS, _avgV));
    }

    private void Report(string? status)
    {
        if (_status == status)
            return;
        _status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        _disposed = true;
        _active = false;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _wake.Dispose();
    }
}
