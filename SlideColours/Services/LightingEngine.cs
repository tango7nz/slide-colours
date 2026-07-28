using System.Windows.Media;
using SlideColours.Models;
using WpfColor = System.Windows.Media.Color;

namespace SlideColours.Services;

/// <summary>Where the stage colour comes from.</summary>
public enum ColourMode
{
    /// <summary>The dominant colour of each ProPresenter slide as it goes live.</summary>
    Slide,
    /// <summary>The colour of a display, sampled continuously — follows animated backgrounds.</summary>
    Screen,
    /// <summary>A colour the user picked; slides and the screen are ignored.</summary>
    Manual,
}

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

    /// <summary>
    /// Fade applied to screen-captured colours. The capture source has already smoothed them over
    /// time, so this only needs to take the step out of consecutive frames — the user's fade time
    /// is for slide changes and would lag hopelessly behind video.
    /// </summary>
    private const int ScreenFadeMs = 120;

    private IDmxOutput? _output;
    private DateTime _outputRetryAt = DateTime.MinValue;
    private double _curR, _curG, _curB;
    private double _tgtR, _tgtG, _tgtB;
    private int _fadeMs;
    private DateTime _testUntil = DateTime.MinValue;
    private WpfColor _lastNotified;
    private bool _lastSending;
    private bool _notifiedOnce;

    public bool Enabled { get; set; }

    private volatile ColourMode _mode = ColourMode.Slide;
    /// <summary>Where the stage colour is currently coming from.</summary>
    public ColourMode Mode => _mode;

    /// <summary>Fired (from a background thread) when the visible output colour or sending state changes.</summary>
    public event Action<WpfColor, bool>? OutputChanged;
    /// <summary>Fired when the DMX output fails or recovers; null = healthy.</summary>
    public event Action<string?>? OutputError;
    /// <summary>Fired when the colour mode changes.</summary>
    public event Action<ColourMode>? ModeChanged;

    public LightingEngine(AppSettings settings)
    {
        _settings = settings;
        _fadeMs = settings.FadeMs;
    }

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
        if (_mode != ColourMode.Slide)
            return; // following the screen, or holding a manual colour

        Task.Run(() =>
        {
            try
            {
                WpfColor? dominant = ColorExtractor.Extract(imageBytes);
                if (dominant is { } c)
                    Apply(c, _settings.FadeMs);
                else
                    ApplyFallback(_settings.FadeMs);
            }
            catch
            {
                // undecodable image — keep the current colour
            }
        });
    }

    public void OnSlideCleared()
    {
        if (_mode == ColourMode.Slide)
            ApplyFallback(_settings.FadeMs);
    }

    /// <summary>
    /// A colour sampled from the captured display, already smoothed over time by
    /// <see cref="ScreenColourSource"/>. Null means the screen carries no usable hue.
    /// </summary>
    public void OnScreenColour(WpfColor? colour)
    {
        if (_mode != ColourMode.Screen)
            return;

        if (colour is { } c)
            Apply(c, ScreenFadeMs);
        else
            ApplyFallback(ScreenFadeMs);
    }

    /// <summary>Hold a manually chosen colour, ignoring slides and the screen until <see cref="SetMode"/> is called.</summary>
    public void SetManualColour(WpfColor colour)
    {
        SetMode(ColourMode.Manual);
        SetTarget(colour.R, colour.G, colour.B, _settings.FadeMs);
    }

    /// <summary>Switches where the stage colour comes from. The colour itself updates on the next sample.</summary>
    public void SetMode(ColourMode mode)
    {
        if (_mode == mode)
            return;
        _mode = mode;
        ModeChanged?.Invoke(mode);
    }

    private void Apply(WpfColor colour, int fadeMs)
    {
        var shaped = ColorExtractor.ShapeForLighting(
            colour, _settings.SaturationBoostPercent / 100.0, _settings.FullBrightnessColours);
        SetTarget(shaped.R, shaped.G, shaped.B, fadeMs);
    }

    private void ApplyFallback(int fadeMs)
    {
        switch (_settings.Fallback)
        {
            case "off": SetTarget(0, 0, 0, fadeMs); break;
            case "white": SetTarget(255, 214, 170, fadeMs); break; // warm white
            default: break;                                        // keep last colour
        }
    }

    private void SetTarget(byte r, byte g, byte b, int fadeMs)
    {
        _tgtR = r; _tgtG = g; _tgtB = b;
        _fadeMs = fadeMs;
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
                    SetTarget(c.R, c.G, c.B, _settings.FadeMs);
                }

                double maxStep = 255.0 * dtMs / Math.Max(50, _fadeMs);
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
