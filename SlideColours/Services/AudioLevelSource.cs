using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SlideColours.Models;

namespace SlideColours.Services;

/// <summary>
/// Listens to an audio feed and reports how hard the bass is hitting, as a level from 0 to 1. That
/// level rides the intensity of any group set to breathe — the colour is left entirely alone.
///
/// Only the bass band is measured, which is what makes this usable in a room rather than only off a
/// clean feed. Kick and bass guitar live around 40-120 Hz; speech fundamentals sit above that with
/// most of their energy higher still, so someone talking near an open mic barely registers. The
/// level is then measured against a noise gate, so anything below the level you set counts as
/// silence and the lights simply settle.
///
/// A line feed from the desk is still much the better input — see <see cref="List"/> — because you
/// choose what's in it. But the band-limiting means a room mic degrades gracefully instead of
/// turning every cough into a light cue.
/// </summary>
public class AudioLevelSource : IDisposable
{
    /// <summary>The bass band, in Hz. Below this is rumble and handling noise; above it is voice.</summary>
    private const double BandLowHz = 40;
    private const double BandHighHz = 120;

    /// <summary>
    /// How fast the adaptive ceiling falls, in dB per second. The ceiling is what makes the feed's
    /// absolute gain irrelevant: the loudest recent bass reads as full. Falling slowly means a
    /// quiet verse still looks quiet, while a gain change at the desk sorts itself out in seconds.
    /// </summary>
    private const double CeilingFallDbPerSecond = 6;

    /// <summary>Narrowest gate-to-ceiling range, so near-silence can't be stretched into a full swing.</summary>
    private const double MinRangeDb = 12;

    /// <summary>
    /// The rise is this many times quicker than the fall. A beat should arrive rather than ooze in,
    /// but the fall is what stops it looking like a strobe, so only the fall is worth exposing.
    /// </summary>
    private const int RiseRatio = 6;

    /// <summary>Longest gap between buffers before the level is decayed anyway — see <see cref="Tick"/>.</summary>
    private static readonly TimeSpan BufferTimeout = TimeSpan.FromMilliseconds(150);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly AppSettings _settings;
    private readonly object _sync = new();
    private readonly object _envelope = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private volatile bool _active;
    private volatile bool _disposed;
    private volatile bool _restart;
    private volatile bool _failed;
    private Thread? _thread;

    private WasapiCapture? _capture;
    private WaveFormat? _format;
    private Biquad? _highPass;
    private Biquad? _lowPass;

    private double _level;
    private double _ceilingDb;

    /// <summary>
    /// When the last buffer arrived, in stopwatch ticks. Written by NAudio's thread and read by the
    /// loop thread's <see cref="Tick"/>, hence the interlocked access.
    /// </summary>
    private long _lastBufferTicks;

    private TimeSpan _lastReport;
    private string? _status;

    /// <summary>
    /// Published while nothing is being listened to. Full, not zero: the level is a ceiling on the
    /// brightness of breathing groups, so "not listening" has to mean "don't hold anything down"
    /// or a group would sit at the bottom of its swing between the device stopping and starting.
    /// </summary>
    private const double NoInfluence = 1.0;

    /// <summary>Fired from the capture thread with the current bass level, 0 to 1.</summary>
    public event Action<double>? LevelSampled;

    /// <summary>Fired when capture breaks or recovers; null = healthy.</summary>
    public event Action<string?>? StatusChanged;

    /// <summary>Last reported problem, or null when healthy.</summary>
    public string? Status => _status;

    /// <summary>The latest level, for meters that would rather poll than take an event 100×/second.</summary>
    public double Level
    {
        get { lock (_envelope) return _level; }
    }

    public AudioLevelSource(AppSettings settings) => _settings = settings;

    /// <summary>Starts or stops listening. Cheap to call repeatedly with the same value.</summary>
    public void SetActive(bool active)
    {
        if (_active == active || _disposed)
            return;

        _active = active;
        if (active)
        {
            lock (_sync)
            {
                if (_thread == null)
                {
                    _thread = new Thread(Loop) { IsBackground = true, Name = "Audio level" };
                    _thread.Start();
                }
            }
        }
        _wake.Set();
    }

    /// <summary>Call after the audio settings change so the next buffer uses the new device.</summary>
    public void Restart()
    {
        _restart = true;
        _wake.Set();
    }

    /// <summary>
    /// The audio inputs that can drive the lights, best first.
    ///
    /// Recording devices come first because that's where a line feed from the sound desk arrives,
    /// and a feed you control the contents of is the whole answer to "won't it hear the sound desk
    /// talking?" — send it only the band channels and it physically cannot. Playback devices are
    /// listed after, captured as loopback, which is the no-cabling option and works whenever this
    /// PC is the thing playing the music.
    /// </summary>
    public static List<AudioDeviceInfo> List()
    {
        var devices = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                using (device)
                    devices.Add(new AudioDeviceInfo($"capture:{device.ID}", $"{device.FriendlyName} (input)"));

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (device)
                    devices.Add(new AudioDeviceInfo($"render:{device.ID}", $"{device.FriendlyName} (what this PC plays)"));
        }
        catch
        {
            // No audio service, or no devices at all — the caller still offers the default entry.
        }
        return devices;
    }

    private void Loop()
    {
        var lastFailure = TimeSpan.MinValue;

        try
        {
            while (!_disposed)
            {
                // As with screen capture, nothing may throw out of this loop: it's the only thing
                // keeping audio alive, so a failure has to back off and retry rather than exit.
                try
                {
                    if (!_active)
                    {
                        StopCapture();
                        _wake.WaitOne(1000);
                        continue;
                    }

                    if (_restart || _failed)
                    {
                        // _failed means the device went away mid-service — a USB interface knocked
                        // out, or the default endpoint switched. Rebuild rather than going quiet.
                        bool wasFailure = _failed;
                        _restart = false;
                        _failed = false;
                        StopCapture();
                        if (wasFailure)
                            lastFailure = _clock.Elapsed;
                    }

                    if (_capture == null)
                    {
                        if (lastFailure != TimeSpan.MinValue && _clock.Elapsed - lastFailure < RetryDelay)
                        {
                            _wake.WaitOne(250);
                            continue;
                        }

                        try
                        {
                            StartCapture();
                            Report(null);
                        }
                        catch (Exception ex)
                        {
                            lastFailure = _clock.Elapsed;
                            Report($"Audio input failed: {ex.Message}");
                            continue;
                        }
                    }

                    Tick();
                    _wake.WaitOne(30);
                }
                catch (Exception ex)
                {
                    StopCapture();
                    lastFailure = _clock.Elapsed;
                    Report($"Audio input failed: {ex.Message}");
                }
            }
        }
        finally
        {
            StopCapture();
            lock (_sync)
                _thread = null;
        }
    }

    private void StartCapture()
    {
        using var enumerator = new MMDeviceEnumerator();
        var (device, loopback) = Resolve(enumerator, _settings.AudioDeviceId);

        using (device)
        {
            var capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
            _format = capture.WaveFormat;

            if (!Supported(_format))
            {
                capture.Dispose();
                throw new NotSupportedException(
                    $"that device records {_format.BitsPerSample}-bit {_format.Encoding}, which isn't supported");
            }

            // Fresh filters and a fresh ceiling: state from the previous device means nothing here.
            _highPass = Biquad.HighPass(_format.SampleRate, BandLowHz);
            _lowPass = Biquad.LowPass(_format.SampleRate, BandHighHz);
            lock (_envelope)
            {
                _level = 0;
                _ceilingDb = Gate() + MinRangeDb;
            }
            Interlocked.Exchange(ref _lastBufferTicks, _clock.Elapsed.Ticks);

            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            capture.StartRecording();
            _capture = capture;
        }
    }

    /// <summary>
    /// Turns a stored setting back into a device. Blank means the default playback device captured
    /// as loopback, which is the one choice guaranteed to exist on any PC.
    /// </summary>
    private static (MMDevice Device, bool Loopback) Resolve(MMDeviceEnumerator enumerator, string setting)
    {
        if (setting.StartsWith("capture:", StringComparison.Ordinal))
            return (enumerator.GetDevice(setting["capture:".Length..]), false);
        if (setting.StartsWith("render:", StringComparison.Ordinal))
            return (enumerator.GetDevice(setting["render:".Length..]), true);

        return (enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia), true);
    }

    private static bool Supported(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat
            ? format.BitsPerSample == 32
            : format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 16 or 24 or 32;

    private void StopCapture()
    {
        var capture = _capture;
        _capture = null;
        if (capture == null)
            return;

        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnStopped;
        try
        {
            capture.StopRecording();
        }
        catch
        {
            // Already stopped, or the device vanished — disposing is all that's left to do.
        }
        capture.Dispose();

        // The meter reads _level and should show nothing; the engine is told to stop dimming.
        lock (_envelope)
            _level = 0;
        _lastReport = _clock.Elapsed;
        LevelSampled?.Invoke(NoInfluence);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        // Only ever raised by the device going away or an error; a deliberate stop unsubscribes
        // first. Flag it and let the loop thread rebuild — this is NAudio's thread, not ours.
        _failed = true;
        _wake.Set();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_format is not { } format || _highPass == null || _lowPass == null)
            return;

        int bytesPerSample = format.BitsPerSample / 8;
        int channels = Math.Max(1, format.Channels);
        int frameBytes = bytesPerSample * channels;
        if (frameBytes == 0 || e.BytesRecorded < frameBytes)
            return;

        double sumSquares = 0;
        int frames = 0;

        for (int offset = 0; offset + frameBytes <= e.BytesRecorded; offset += frameBytes)
        {
            // Downmix first: bass is nearly always centred, and one filter pass is cheaper than one
            // per channel.
            double mono = 0;
            for (int c = 0; c < channels; c++)
                mono += Sample(e.Buffer, offset + c * bytesPerSample, format);
            mono /= channels;

            double bass = _lowPass.Process(_highPass.Process(mono));
            sumSquares += bass * bass;
            frames++;
        }

        if (frames == 0)
            return;

        var now = _clock.Elapsed;
        long previous = Interlocked.Exchange(ref _lastBufferTicks, now.Ticks);
        double elapsedMs = (now - TimeSpan.FromTicks(previous)).TotalMilliseconds;

        double rms = Math.Sqrt(sumSquares / frames);
        Advance(Math.Max(rms, 1e-7), elapsedMs);
    }

    private static double Sample(byte[] buffer, int index, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            return BitConverter.ToSingle(buffer, index);

        return format.BitsPerSample switch
        {
            16 => BitConverter.ToInt16(buffer, index) / 32768.0,
            24 => ((sbyte)buffer[index + 2] << 16 | buffer[index + 1] << 8 | buffer[index]) / 8388608.0,
            _ => BitConverter.ToInt32(buffer, index) / 2147483648.0,
        };
    }

    /// <summary>
    /// Folds one buffer's bass energy into the reported level: gate, adaptive ceiling, then an
    /// asymmetric smooth so it rises with the beat and falls gently.
    /// </summary>
    private void Advance(double rms, double elapsedMs)
    {
        double gate = Gate();
        double fallMs = Math.Clamp(_settings.AudioResponseMs, 50, 2000);
        double db = 20 * Math.Log10(rms);
        double published;

        lock (_envelope)
        {
            _ceilingDb = Math.Max(db, _ceilingDb - CeilingFallDbPerSecond * elapsedMs / 1000.0);
            _ceilingDb = Math.Max(_ceilingDb, gate + MinRangeDb);

            double target = db <= gate ? 0 : Math.Clamp((db - gate) / (_ceilingDb - gate), 0, 1);
            double tau = target > _level ? fallMs / RiseRatio : fallMs;
            double alpha = 1 - Math.Exp(-Math.Clamp(elapsedMs, 1, 500) / tau);

            _level += alpha * (target - _level);
            published = _level;
        }

        Publish(published);
    }

    /// <summary>
    /// Decays the level when buffers stop arriving. Loopback capture can go quiet altogether when
    /// nothing is playing, and a device pulled mid-song delivers nothing more — either way the
    /// lights must settle rather than freezing at whatever the last beat left behind.
    /// </summary>
    private void Tick()
    {
        var now = _clock.Elapsed;
        if (now - TimeSpan.FromTicks(Interlocked.Read(ref _lastBufferTicks)) < BufferTimeout)
            return;

        Interlocked.Exchange(ref _lastBufferTicks, now.Ticks);
        Advance(1e-7, BufferTimeout.TotalMilliseconds);
    }

    private double Gate() => Math.Clamp(_settings.AudioGateDb, -80, -10);

    /// <summary>
    /// Hands the level on, at most 60×/second. Buffers can arrive far faster than that and the
    /// consumer is a 30fps DMX loop, so the extra calls would be pure lock traffic.
    /// </summary>
    private void Publish(double level)
    {
        var now = _clock.Elapsed;
        if (level > 0 && (now - _lastReport).TotalMilliseconds < 1000.0 / 60)
            return;
        _lastReport = now;
        LevelSampled?.Invoke(level);
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

    /// <summary>
    /// A second-order section, used here as a 40-120 Hz band built from a high-pass and a low-pass.
    /// Butterworth Q, so the two together are flat across the band. Doubles throughout: at 48 kHz a
    /// 40 Hz corner puts the poles very close to the unit circle, where float loses its footing.
    /// </summary>
    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        private const double ButterworthQ = 0.70710678;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0;
            _a1 = a1 / a0; _a2 = a2 / a0;
        }

        public static Biquad LowPass(double sampleRate, double freq)
        {
            var (cos, alpha) = Terms(sampleRate, freq);
            return new Biquad((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        public static Biquad HighPass(double sampleRate, double freq)
        {
            var (cos, alpha) = Terms(sampleRate, freq);
            return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        private static (double Cos, double Alpha) Terms(double sampleRate, double freq)
        {
            // Guard against a device reporting an odd rate: the corner must stay below Nyquist.
            double w0 = 2 * Math.PI * Math.Min(freq, sampleRate / 2.5) / sampleRate;
            return (Math.Cos(w0), Math.Sin(w0) / (2 * ButterworthQ));
        }

        public double Process(double x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }
    }
}

/// <summary>One selectable audio input. <paramref name="Id"/> is stored in the settings file.</summary>
/// <param name="Id">"capture:{deviceId}" for an input, "render:{deviceId}" for loopback.</param>
public readonly record struct AudioDeviceInfo(string Id, string Label);
