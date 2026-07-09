using System.Windows.Media;
using SlideColours.Models;
using WpfColor = System.Windows.Media.Color;

namespace SlideColours.Services;

/// <summary>
/// Owns the DMX output: fades the current colour towards the target and, while enabled,
/// transmits DMX frames ~30 times per second.
/// </summary>
public class LightingEngine : IDisposable
{
    private readonly AppSettings _settings;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _frame = new byte[512];

    private IDmxOutput? _output;
    private DateTime _outputRetryAt = DateTime.MinValue;
    private double _curR, _curG, _curB;
    private double _tgtR, _tgtG, _tgtB;
    private DateTime _testUntil = DateTime.MinValue;
    private WpfColor _lastNotified;
    private bool _lastSending;
    private bool _notifiedOnce;

    public bool Enabled { get; set; }

    private volatile bool _followSlides = true;
    /// <summary>True = colour follows the live slide; false = holding a manually chosen colour.</summary>
    public bool FollowSlides => _followSlides;

    /// <summary>Fired (from a background thread) when the visible output colour or sending state changes.</summary>
    public event Action<WpfColor, bool>? OutputChanged;
    /// <summary>Fired when the DMX output fails or recovers; null = healthy.</summary>
    public event Action<string?>? OutputError;
    /// <summary>Fired when the colour mode changes; true = following the live slide.</summary>
    public event Action<bool>? ModeChanged;

    public LightingEngine(AppSettings settings) => _settings = settings;

    public void Start() => Task.Run(LoopAsync);

    /// <summary>Call after settings change so the DMX output is rebuilt with the new protocol/target.</summary>
    public void RefreshOutput()
    {
        lock (_sync)
        {
            _output?.Dispose();
            _output = null;
            _outputRetryAt = DateTime.MinValue;
        }
    }

    /// <summary>Runs an 8-second rainbow sweep, sending DMX even if the toggle is off — for checking patching.</summary>
    public void RunTestSweep() => _testUntil = DateTime.UtcNow.AddSeconds(8);

    public void OnSlideImage(byte[] imageBytes)
    {
        if (!_followSlides)
            return; // holding a manual colour — ignore slide changes

        Task.Run(() =>
        {
            try
            {
                WpfColor? dominant = ColorExtractor.Extract(imageBytes);
                if (dominant is { } c)
                {
                    var shaped = ColorExtractor.ShapeForLighting(
                        c, _settings.SaturationBoostPercent / 100.0, _settings.FullBrightnessColours);
                    SetTarget(shaped.R, shaped.G, shaped.B);
                }
                else
                {
                    ApplyFallback();
                }
            }
            catch
            {
                // undecodable image — keep the current colour
            }
        });
    }

    public void OnSlideCleared()
    {
        if (_followSlides)
            ApplyFallback();
    }

    /// <summary>Hold a manually chosen colour, ignoring slide changes until <see cref="SetFollowSlides"/> is called.</summary>
    public void SetManualColour(System.Windows.Media.Color colour)
    {
        if (_followSlides)
        {
            _followSlides = false;
            ModeChanged?.Invoke(false);
        }
        SetTarget(colour.R, colour.G, colour.B);
    }

    /// <summary>Return to following the live slide; the colour updates on the next slide change.</summary>
    public void SetFollowSlides()
    {
        if (!_followSlides)
        {
            _followSlides = true;
            ModeChanged?.Invoke(true);
        }
    }

    private void ApplyFallback()
    {
        switch (_settings.Fallback)
        {
            case "off": SetTarget(0, 0, 0); break;
            case "white": SetTarget(255, 214, 170); break; // warm white
            default: break;                                // keep last colour
        }
    }

    private void SetTarget(byte r, byte g, byte b)
    {
        _tgtR = r; _tgtG = g; _tgtB = b;
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        var last = DateTime.UtcNow;
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                var now = DateTime.UtcNow;
                double dtMs = (now - last).TotalMilliseconds;
                last = now;

                bool testing = now < _testUntil;
                if (testing)
                {
                    // full rainbow every 8 seconds
                    double hue = now.TimeOfDay.TotalMilliseconds / 8000.0 % 1.0 * 360.0;
                    var c = ColorExtractor.HsvToRgb(hue, 1, 1);
                    SetTarget(c.R, c.G, c.B);
                }

                double maxStep = 255.0 * dtMs / Math.Max(50, _settings.FadeMs);
                _curR = MoveToward(_curR, _tgtR, maxStep);
                _curG = MoveToward(_curG, _tgtG, maxStep);
                _curB = MoveToward(_curB, _tgtB, maxStep);

                bool sending = Enabled || testing;
                if (sending)
                    Transmit();

                var visible = WpfColor.FromRgb((byte)Math.Round(_curR), (byte)Math.Round(_curG), (byte)Math.Round(_curB));
                if (!_notifiedOnce || visible != _lastNotified || sending != _lastSending)
                {
                    _notifiedOnce = true;
                    _lastNotified = visible;
                    _lastSending = sending;
                    OutputChanged?.Invoke(visible, sending);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Transmit()
    {
        lock (_sync)
        {
            if (_output == null)
            {
                if (DateTime.UtcNow < _outputRetryAt)
                    return;
                try
                {
                    _output = CreateOutput();
                    OutputError?.Invoke(null);
                }
                catch (Exception ex)
                {
                    _outputRetryAt = DateTime.UtcNow.AddSeconds(3);
                    OutputError?.Invoke($"DMX output failed: {ex.Message}");
                    return;
                }
            }

            try
            {
                BuildFrame();
                _output.SendFrame(_frame);
            }
            catch (Exception ex)
            {
                _output.Dispose();
                _output = null;
                _outputRetryAt = DateTime.UtcNow.AddSeconds(3);
                OutputError?.Invoke($"DMX send failed: {ex.Message}");
            }
        }
    }

    private IDmxOutput CreateOutput() => _settings.Protocol switch
    {
        "sacn" => new SacnOutput(_settings.TargetIp, _settings.Universe),
        "enttec" => new EnttecProOutput(_settings.ComPort),
        _ => new ArtNetOutput(_settings.TargetIp, _settings.Universe),
    };

    private void BuildFrame()
    {
        Array.Clear(_frame);
        double brightness = Math.Clamp(_settings.BrightnessPercent, 0, 100) / 100.0;
        int i = Math.Clamp(_settings.StartChannel, 1, 512) - 1;
        double scale = brightness;

        if (_settings.HasMasterDimmer)
        {
            _frame[i++] = (byte)Math.Round(255 * brightness);
            scale = 1.0; // dimmer handles intensity, colour channels stay pure
        }
        if (i < 512) _frame[i++] = (byte)Math.Round(_curR * scale);
        if (i < 512) _frame[i++] = (byte)Math.Round(_curG * scale);
        if (i < 512) _frame[i] = (byte)Math.Round(_curB * scale);
    }

    private static double MoveToward(double current, double target, double maxStep)
    {
        double delta = target - current;
        if (Math.Abs(delta) <= maxStep)
            return target;
        return current + Math.Sign(delta) * maxStep;
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_sync)
        {
            _output?.Dispose();
            _output = null;
        }
    }
}
