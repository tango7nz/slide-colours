using SlideColours.Models;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace SlideColours.Services;

/// <summary>
/// Owns the DMX output. Every group fades its own colour towards its own target, and while enabled
/// the merged result is transmitted ~30 times per second.
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

    /// <summary>Live fade state for one group. Rebuilt from settings by <see cref="ReloadRig"/>.</summary>
    private sealed class GroupState
    {
        public required LightGroup Config { get; init; }
        public required List<Fixture> Fixtures { get; init; }
        public double CurR, CurG, CurB;
        public double TgtR, TgtG, TgtB;
        public int FadeMs;
    }

    private IDmxOutput? _output;
    private DateTime _outputRetryAt = DateTime.MinValue;
    private List<GroupState> _groups = new();

    /// <summary>
    /// The bass level driving groups set to breathe, 0 to 1. Starts at full so nothing is dimmed
    /// before the first buffer arrives. Guarded by <see cref="_sync"/>.
    /// </summary>
    private double _audioLevel = 1.0;

    /// <summary>False while the audio input is broken — see <see cref="SetAudioHealthy"/>.</summary>
    private bool _audioHealthy = true;

    /// <summary>
    /// Fixtures the patch test is walking, one at a time. Empty when no test is running. These are
    /// the caller's own <see cref="Fixture"/> objects, so the rig editor can test an edited channel
    /// before it's been saved.
    /// </summary>
    private List<Fixture> _testSequence = new();
    private DateTime _testStart;
    private Fixture? _testCurrent;
    private double _testR, _testG, _testB;

    /// <summary>How long each fixture is lit for, split evenly between the four test colours.</summary>
    private const double TestSecondsPerFixture = 2.0;

    /// <summary>
    /// Red, green, blue then white. Stepping through the primaries proves the channels are in the
    /// right order — a fixture patched one channel out shows the wrong colours, which a plain white
    /// flash would hide.
    /// </summary>
    private static readonly WpfColor[] TestColours =
    {
        WpfColor.FromRgb(255, 0, 0),
        WpfColor.FromRgb(0, 255, 0),
        WpfColor.FromRgb(0, 0, 255),
        WpfColor.FromRgb(255, 255, 255),
    };
    private WpfColor _lastNotified;
    private bool _lastSending;
    private volatile bool _notifiedOnce;
    private bool _lastScreenNeeded;
    private bool _lastAudioNeeded;

    private volatile bool _enabled;

    /// <summary>
    /// Master output on/off. Settable from the remote as well as the pill, so a change raises
    /// <see cref="EnabledChanged"/> — the toggle in the window has no other way to learn about it.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            EnabledChanged?.Invoke(value);
        }
    }

    private volatile string _activeGroupId = "";

    /// <summary>
    /// The scene most recently recalled, whether from the pill, settings or the remote. Only used to
    /// break ties in <see cref="ActiveScene"/>; a stale id costs nothing because it is checked
    /// against the live state before it is trusted.
    /// </summary>
    private volatile string _lastAppliedSceneId = "";

    /// <summary>The group the main window's controls act on; drives <see cref="OutputChanged"/>.</summary>
    public string ActiveGroupId
    {
        get => _activeGroupId;
        set
        {
            if (_activeGroupId == value)
                return;
            _activeGroupId = value;
            _settings.ActiveGroupId = value;
            GroupsChanged?.Invoke();
        }
    }

    /// <summary>True while any group is following the screen, so capture can be left off otherwise.</summary>
    public bool ScreenNeeded
    {
        get
        {
            lock (_sync)
                return _groups.Any(g => g.Config.Source == ColourSources.Screen);
        }
    }

    /// <summary>True while any group is following slides, so slide images are worth decoding.</summary>
    private bool SlideNeeded
    {
        get
        {
            lock (_sync)
                return _groups.Any(g => g.Config.Source == ColourSources.Slide);
        }
    }

    /// <summary>True while any group is breathing, so the audio device is only opened when it's used.</summary>
    public bool AudioNeeded
    {
        get
        {
            lock (_sync)
                return _groups.Any(g => g.Config.Pulse == PulseModes.Breathe);
        }
    }

    /// <summary>Fired (from a background thread) when the active group's colour or sending state changes.</summary>
    public event Action<WpfColor, bool>? OutputChanged;
    /// <summary>Fired when the DMX output fails or recovers; null = healthy.</summary>
    public event Action<string?>? OutputError;
    /// <summary>Fired when groups, their sources, or the active group change.</summary>
    public event Action? GroupsChanged;
    /// <summary>Fired when master output is switched on or off, from wherever.</summary>
    public event Action<bool>? EnabledChanged;
    /// <summary>Fired when screen capture becomes needed or unneeded.</summary>
    public event Action<bool>? ScreenNeededChanged;
    /// <summary>Fired when audio capture becomes needed or unneeded.</summary>
    public event Action<bool>? AudioNeededChanged;

    public LightingEngine(AppSettings settings)
    {
        _settings = settings;
        _activeGroupId = settings.ResolveStartupGroupId();
        ReloadRig();

        // A pinned startup scene deliberately overrides whatever the groups were left doing, so
        // every service opens on the same defined look rather than on last week's closing song.
        if (settings.StartupScene is { } scene)
            ApplyScene(scene);
    }

    public void Start() => Task.Run(LoopAsync);

    /// <summary>
    /// Rebuilds the group list from settings. Call after the rig or the DMX endpoint is edited.
    /// Groups that still exist keep the colour they are currently showing, so saving settings
    /// mid-service doesn't blink the lights back through black.
    /// </summary>
    public void ReloadRig()
    {
        lock (_sync)
        {
            var previous = _groups.ToDictionary(g => g.Config.Id);
            var byId = _settings.Fixtures.ToDictionary(f => f.Id);

            _groups = _settings.Groups.Select(group =>
            {
                var state = new GroupState
                {
                    Config = group,
                    Fixtures = group.FixtureIds
                        .Where(byId.ContainsKey)
                        .Select(id => byId[id])
                        .ToList(),
                    FadeMs = _settings.FadeMs,
                };
                if (previous.TryGetValue(group.Id, out var old))
                {
                    state.CurR = old.CurR; state.CurG = old.CurG; state.CurB = old.CurB;
                    state.TgtR = old.TgtR; state.TgtG = old.TgtG; state.TgtB = old.TgtB;
                }
                return state;
            }).ToList();

            // "manual" and "off" groups get no external updates, so seed their target now —
            // otherwise a newly added group would sit at black until its source changed.
            foreach (var state in _groups)
                SeedStaticTarget(state);

            _output?.Dispose();
            _output = null;
            _outputRetryAt = DateTime.MinValue;
        }

        if (!_settings.Groups.Any(g => g.Id == _activeGroupId) && _settings.Groups.Count > 0)
            _activeGroupId = _settings.Groups[0].Id;

        GroupsChanged?.Invoke();
        RaiseSourceNeeds();
    }

    /// <summary>
    /// Lights the given fixtures one at a time — red, green, blue, white — so you can see which
    /// light is which and that its channels are in the right order. Every other fixture is held
    /// dark for the duration, and it transmits even when the output toggle is off.
    ///
    /// Pass a single fixture to identify one light; pass the whole rig to walk it.
    /// </summary>
    public void RunPatchTest(IEnumerable<Fixture> fixtures)
    {
        var sequence = fixtures.ToList();
        lock (_sync)
        {
            _testSequence = sequence;
            _testStart = DateTime.UtcNow;
        }
    }

    public void StopPatchTest()
    {
        lock (_sync)
            _testSequence = new List<Fixture>();
    }

    public bool TestRunning
    {
        get { lock (_sync) return _testSequence.Count > 0; }
    }

    /// <summary>Fired as the patch test moves to the next fixture; null when it finishes or is stopped.</summary>
    public event Action<Fixture?>? TestFixtureChanged;

    /// <summary>
    /// Makes the next frame re-raise <see cref="OutputChanged"/> even if nothing moved. The engine
    /// starts before the window exists, so a colour that has already settled — a group holding a
    /// manual colour restored from settings — would otherwise never be announced to a late
    /// subscriber, leaving the swatch black.
    /// </summary>
    public void ResendOutputState() => _notifiedOnce = false;

    /// <summary>Points a group at a different colour source and persists the change.</summary>
    public void SetGroupSource(string groupId, string source)
    {
        source = ColourSources.Normalise(source);
        lock (_sync)
        {
            var state = _groups.FirstOrDefault(g => g.Config.Id == groupId);
            if (state == null || state.Config.Source == source)
                return;
            state.Config.Source = source;
            SeedStaticTarget(state);
        }
        _settings.Save();
        GroupsChanged?.Invoke();
        RaiseSourceNeeds();
    }

    /// <summary>Holds a chosen colour on a group, switching it to the manual source.</summary>
    public void SetGroupManualColour(string groupId, WpfColor colour)
    {
        lock (_sync)
        {
            var state = _groups.FirstOrDefault(g => g.Config.Id == groupId);
            if (state == null)
                return;
            state.Config.Source = ColourSources.Manual;
            state.Config.ManualColour = $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";
            SetTarget(state, colour.R, colour.G, colour.B, _settings.FadeMs);
        }
        _settings.Save();
        GroupsChanged?.Invoke();
        RaiseSourceNeeds();
    }

    /// <summary>
    /// Recalls a saved look: every group the scene lists is switched to its stored source and
    /// colour. Groups added since the scene was saved aren't mentioned by it and are left running
    /// as they are. The scene itself is never modified — see <see cref="CaptureScene"/> for that.
    /// </summary>
    public void ApplyScene(Scene scene)
    {
        _lastAppliedSceneId = scene.Id;
        lock (_sync)
        {
            foreach (var state in _groups)
            {
                if (scene.For(state.Config.Id) is not { } entry)
                    continue;

                state.Config.Source = ColourSources.Normalise(entry.Source);
                if (state.Config.Source == ColourSources.Manual)
                    state.Config.ManualColour = entry.ManualColour;
                state.Config.Pulse = PulseModes.Normalise(entry.Pulse);
                state.Config.PulseDepthPercent = Math.Clamp(entry.PulseDepthPercent, 0, 100);
                SeedStaticTarget(state);
            }
        }
        _settings.Save();
        GroupsChanged?.Invoke();
        RaiseSourceNeeds();
    }

    /// <summary>Snapshots what every group is doing right now, for saving into a scene.</summary>
    public List<SceneGroupState> CaptureScene()
    {
        lock (_sync)
        {
            return _groups.Select(g => new SceneGroupState
            {
                GroupId = g.Config.Id,
                Source = g.Config.Source,
                ManualColour = g.Config.ManualColour,
                Pulse = g.Config.Pulse,
                PulseDepthPercent = g.Config.PulseDepthPercent,
            }).ToList();
        }
    }

    /// <summary>
    /// The scene the stage is showing, or null once anything has been nudged by hand. Two scenes
    /// can describe an identical look, so a scene that was actually recalled wins over an equally
    /// good match earlier in the list — otherwise recalling "MC" would report its twin "Worship".
    /// </summary>
    public Scene? ActiveScene()
    {
        var scenes = _settings.Scenes.ToList();
        string last = _lastAppliedSceneId;
        var recalled = scenes.FirstOrDefault(s => s.Id == last);
        if (recalled != null && MatchesScene(recalled))
            return recalled;

        return scenes.FirstOrDefault(MatchesScene);
    }

    /// <summary>
    /// True when every group the scene mentions is currently doing exactly what it says. Lets the
    /// main window show which scene is live, and fall back to "custom" once anything is nudged.
    /// </summary>
    public bool MatchesScene(Scene scene)
    {
        lock (_sync)
        {
            if (scene.Groups.Count == 0)
                return false;
            foreach (var entry in scene.Groups)
            {
                var state = _groups.FirstOrDefault(g => g.Config.Id == entry.GroupId);
                if (state == null || !entry.Matches(state.Config))
                    return false;
            }
            return true;
        }
    }

    /// <summary>The source a group is currently using, or "off" for an unknown group.</summary>
    public string SourceOf(string groupId)
    {
        lock (_sync)
            return _groups.FirstOrDefault(g => g.Config.Id == groupId)?.Config.Source ?? ColourSources.Off;
    }

    /// <summary>
    /// Turns breathing on or off for a group and persists the change. Deliberately does not touch
    /// the group's colour source — the two are independent, so a group can be switched between
    /// breathing and steady mid-song without disturbing where its colour comes from.
    /// </summary>
    public void SetGroupPulse(string groupId, string pulse)
    {
        pulse = PulseModes.Normalise(pulse);
        lock (_sync)
        {
            var state = _groups.FirstOrDefault(g => g.Config.Id == groupId);
            if (state == null || state.Config.Pulse == pulse)
                return;
            state.Config.Pulse = pulse;
        }
        _settings.Save();
        GroupsChanged?.Invoke();
        RaiseSourceNeeds();
    }

    /// <summary>The pulse mode a group is currently using, or "off" for an unknown group.</summary>
    public string PulseOf(string groupId)
    {
        lock (_sync)
            return _groups.FirstOrDefault(g => g.Config.Id == groupId)?.Config.Pulse ?? PulseModes.Off;
    }

    public void OnSlideImage(byte[] imageBytes)
    {
        if (!SlideNeeded)
            return;

        Task.Run(() =>
        {
            try
            {
                WpfColor? dominant = ColorExtractor.Extract(imageBytes);
                if (dominant is { } c)
                    ApplyToSource(ColourSources.Slide, c, _settings.FadeMs);
                else
                    ApplyFallback(ColourSources.Slide, _settings.FadeMs);
            }
            catch
            {
                // undecodable image — keep the current colour
            }
        });
    }

    public void OnSlideCleared() => ApplyFallback(ColourSources.Slide, _settings.FadeMs);

    /// <summary>
    /// A colour sampled from the captured display, already smoothed over time by
    /// <see cref="ScreenColourSource"/>. Null means the screen carries no usable hue.
    /// </summary>
    public void OnScreenColour(WpfColor? colour)
    {
        if (colour is { } c)
            ApplyToSource(ColourSources.Screen, c, ScreenFadeMs);
        else
            ApplyFallback(ColourSources.Screen, ScreenFadeMs);
    }

    /// <summary>Sends one shaped colour to every group currently listening to <paramref name="source"/>.</summary>
    private void ApplyToSource(string source, WpfColor colour, int fadeMs)
    {
        var shaped = ColorExtractor.ShapeForLighting(
            colour, _settings.SaturationBoostPercent / 100.0, _settings.FullBrightnessColours);

        lock (_sync)
        {
            foreach (var state in _groups.Where(g => g.Config.Source == source))
                SetTarget(state, shaped.R, shaped.G, shaped.B, fadeMs);
        }
    }

    private void ApplyFallback(string source, int fadeMs)
    {
        lock (_sync)
        {
            foreach (var state in _groups.Where(g => g.Config.Source == source))
            {
                switch (_settings.Fallback)
                {
                    case "off": SetTarget(state, 0, 0, 0, fadeMs); break;
                    case "white": SetTarget(state, 255, 214, 170, fadeMs); break; // warm white
                    default: break;                                              // keep last colour
                }
            }
        }
    }

    /// <summary>Sets the target for sources that never receive updates: the manual colour, or black.</summary>
    private void SeedStaticTarget(GroupState state)
    {
        if (state.Config.Source == ColourSources.Off)
        {
            SetTarget(state, 0, 0, 0, _settings.FadeMs);
        }
        else if (state.Config.Source == ColourSources.Manual)
        {
            var colour = ParseColour(state.Config.ManualColour);
            SetTarget(state, colour.R, colour.G, colour.B, _settings.FadeMs);
        }
    }

    private static void SetTarget(GroupState state, byte r, byte g, byte b, int fadeMs)
    {
        state.TgtR = r; state.TgtG = g; state.TgtB = b;
        state.FadeMs = fadeMs;
    }

    private static WpfColor ParseColour(string? value)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
                return (WpfColor)WpfColorConverter.ConvertFromString(value);
        }
        catch
        {
            // hand-edited settings file — fall through
        }
        return System.Windows.Media.Colors.White;
    }

    /// <summary>
    /// Announces which capture sources the current rig actually needs, so neither the display nor
    /// the audio device is opened unless some group is using it. Called after anything that can
    /// change a group's source or pulse mode.
    /// </summary>
    private void RaiseSourceNeeds()
    {
        bool screen = ScreenNeeded;
        if (screen != _lastScreenNeeded)
        {
            _lastScreenNeeded = screen;
            ScreenNeededChanged?.Invoke(screen);
        }

        bool audio = AudioNeeded;
        if (audio != _lastAudioNeeded)
        {
            _lastAudioNeeded = audio;
            AudioNeededChanged?.Invoke(audio);
        }
    }

    /// <summary>The latest bass level from <see cref="AudioLevelSource"/>, 0 to 1.</summary>
    public void OnAudioLevel(double level)
    {
        lock (_sync)
            _audioLevel = Math.Clamp(level, 0, 1);
    }

    /// <summary>
    /// Tells the engine whether the audio input is working. A broken input holds breathing groups
    /// at full brightness rather than at the bottom of their swing — lights that have quietly gone
    /// dim read as a fault on stage, and the reason for it isn't visible from there.
    /// </summary>
    public void SetAudioHealthy(bool healthy)
    {
        lock (_sync)
            _audioHealthy = healthy;
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        var last = DateTime.UtcNow;
        var lastFault = DateTime.MinValue;

        // Nothing but shutdown may end this loop. It is the only thing driving the rig, so dying
        // here stops the lights with no indication whatsoever — the one failure the operator has
        // no way to diagnose from the stage. Everything risky inside already handles itself, so
        // reaching the catch below means something genuinely unforeseen.
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(_cts.Token))
                    break;

                var now = DateTime.UtcNow;
                double dtMs = (now - last).TotalMilliseconds;
                last = now;

                WpfColor visible;
                Fixture? testFixture;
                lock (_sync)
                {
                    testFixture = AdvanceTestLocked(now);
                    foreach (var state in _groups)
                    {
                        double maxStep = 255.0 * dtMs / Math.Max(50, state.FadeMs);
                        state.CurR = MoveToward(state.CurR, state.TgtR, maxStep);
                        state.CurG = MoveToward(state.CurG, state.TgtG, maxStep);
                        state.CurB = MoveToward(state.CurB, state.TgtB, maxStep);
                    }

                    visible = testFixture != null
                        ? Rgb(_testR, _testG, _testB)
                        : ActiveColourLocked();
                }

                if (!ReferenceEquals(testFixture, _testCurrent))
                {
                    _testCurrent = testFixture;
                    TestFixtureChanged?.Invoke(testFixture);
                }

                bool sending = Enabled || testFixture != null;
                if (sending)
                    Transmit(testFixture);

                if (!_notifiedOnce || visible != _lastNotified || sending != _lastSending)
                {
                    _notifiedOnce = true;
                    _lastNotified = visible;
                    _lastSending = sending;
                    OutputChanged?.Invoke(visible, sending);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                var faultedAt = DateTime.UtcNow;
                // Don't let the time spent failing arrive as one enormous fade step.
                last = faultedAt;

                // A fault that persists would otherwise write to the log 30 times a second.
                if (faultedAt - lastFault < TimeSpan.FromSeconds(5))
                    continue;
                lastFault = faultedAt;

                ErrorLog.Write("DMX loop", ex);
                OutputError?.Invoke($"Lighting engine error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Works out which fixture the patch test should be lighting, and in which colour. Returns null
    /// when no test is running or the sequence has finished. Caller must hold <see cref="_sync"/>.
    /// </summary>
    private Fixture? AdvanceTestLocked(DateTime now)
    {
        if (_testSequence.Count == 0)
            return null;

        double elapsed = (now - _testStart).TotalSeconds;
        int index = (int)(elapsed / TestSecondsPerFixture);
        if (index >= _testSequence.Count)
        {
            _testSequence = new List<Fixture>();
            return null;
        }

        // Snap between the four colours rather than fading — crisp steps are much easier to read
        // off a stage than a slow crossfade.
        double within = elapsed - index * TestSecondsPerFixture;
        int step = Math.Clamp(
            (int)(within / (TestSecondsPerFixture / TestColours.Length)), 0, TestColours.Length - 1);
        var colour = TestColours[step];
        _testR = colour.R;
        _testG = colour.G;
        _testB = colour.B;

        return _testSequence[index];
    }

    /// <summary>The colour of the active group. Caller must hold <see cref="_sync"/>.</summary>
    private WpfColor ActiveColourLocked()
    {
        var state = _groups.FirstOrDefault(g => g.Config.Id == _activeGroupId) ?? _groups.FirstOrDefault();
        return state == null ? System.Windows.Media.Colors.Black : Rgb(state.CurR, state.CurG, state.CurB);
    }

    private static WpfColor Rgb(double r, double g, double b) =>
        WpfColor.FromRgb((byte)Math.Round(Math.Clamp(r, 0, 255)),
                         (byte)Math.Round(Math.Clamp(g, 0, 255)),
                         (byte)Math.Round(Math.Clamp(b, 0, 255)));

    private void Transmit(Fixture? testFixture)
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
                BuildFrame(testFixture);
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
        "xpress" => new Xpress512Output(),
        _ => new ArtNetOutput(_settings.TargetIp, _settings.Universe),
    };

    /// <summary>
    /// Paints every group's colour onto its fixtures. While the patch test is running, only the
    /// fixture under test is lit and everything else is held dark — that's what makes it possible
    /// to tell which light is which. Caller holds the lock.
    /// </summary>
    private void BuildFrame(Fixture? testFixture)
    {
        Array.Clear(_frame);
        double master = Math.Clamp(_settings.BrightnessPercent, 0, 100) / 100.0;

        if (testFixture != null)
        {
            WriteFixture(testFixture, _testR, _testG, _testB, master);
            return;
        }

        // The intensity each group ends up at is the master brightness times whatever its own
        // effects are doing. Working it out per group rather than once for the frame is what lets
        // one group breathe with the music while the rest hold steady.
        foreach (var state in _groups)
        {
            double level = master * PulseFactorLocked(state);
            foreach (var fixture in state.Fixtures)
                WriteFixture(fixture, state.CurR, state.CurG, state.CurB, level);
        }
    }

    /// <summary>
    /// How far the music is holding this group's intensity down right now, from 1 (untouched) to
    /// 1 − depth at silence. Applied here at frame-build time rather than as a fade target on
    /// purpose: routed through <see cref="MoveToward"/> the fade time would smear every beat flat.
    /// Caller holds the lock.
    /// </summary>
    private double PulseFactorLocked(GroupState state)
    {
        if (state.Config.Pulse != PulseModes.Breathe || !_audioHealthy)
            return 1.0;

        double depth = Math.Clamp(state.Config.PulseDepthPercent, 0, 100) / 100.0;
        return 1.0 - depth * (1.0 - _audioLevel);
    }

    private void WriteFixture(Fixture fixture, double r, double g, double b, double brightness)
    {
        int i = Math.Clamp(fixture.StartChannel, 1, 512) - 1;
        double scale = brightness;

        if (fixture.HasMasterDimmer)
        {
            Merge(i++, 255 * brightness);
            scale = 1.0; // dimmer handles intensity, colour channels stay pure
        }
        Merge(i++, r * scale);
        Merge(i++, g * scale);
        Merge(i, b * scale);
    }

    /// <summary>
    /// Highest-takes-precedence, the lighting-desk convention for merging sources. A fixture put in
    /// two groups therefore shows the brighter of the two rather than depending on group order.
    /// </summary>
    private void Merge(int index, double value)
    {
        if (index < 0 || index >= _frame.Length)
            return;
        byte level = (byte)Math.Round(Math.Clamp(value, 0, 255));
        if (level > _frame[index])
            _frame[index] = level;
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
