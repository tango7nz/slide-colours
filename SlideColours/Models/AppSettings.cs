using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlideColours.Models;

public class AppSettings
{
    // ProPresenter connection (Preferences -> Network shows the port)
    public string ProPresenterHost { get; set; } = "127.0.0.1";
    public int ProPresenterPort { get; set; } = 64194;

    // DMX output
    public string Protocol { get; set; } = "artnet";   // artnet | sacn | enttec
    public string TargetIp { get; set; } = "";         // blank = broadcast (Art-Net) / multicast (sACN)
    public int Universe { get; set; } = 0;             // Art-Net universes are 0-based, sACN 1-based
    public string ComPort { get; set; } = "COM3";      // Enttec DMX USB Pro only

    // The rig: individual lights, and the groups that give them a colour. Every group runs at once
    // with its own source, so different parts of the stage can behave differently.
    public List<Fixture> Fixtures { get; set; } = new();
    public List<LightGroup> Groups { get; set; } = new();

    /// <summary>Saved looks that set every group at once. See <see cref="Scene"/>.</summary>
    public List<Scene> Scenes { get; set; } = new();

    /// <summary>A scene to recall on launch. Blank leaves the groups as they were last left.</summary>
    public string StartupSceneId { get; set; } = "";

    /// <summary>The group the main window's controls act on. Falls back to the first group.</summary>
    public string ActiveGroupId { get; set; } = "";

    /// <summary>
    /// The group to select when the app opens. Blank means "whichever was selected last", which is
    /// how it behaved before this setting existed.
    /// </summary>
    public string StartupGroupId { get; set; } = "";

    // Single-fixture patch from versions before groups existed. Only read during migration.
    public int StartChannel { get; set; } = 1;         // 1-512, first channel of the fixture
    public bool HasMasterDimmer { get; set; } = false; // true = layout is Dimmer,R,G,B; false = R,G,B

    // Screen capture — colour sampled live from a display (the ProPresenter output), so that
    // animated backgrounds drive the lights the way a TV backlight follows the picture.
    public string ColourSource { get; set; } = "slide";  // legacy pre-groups mode; only read by MigrateRig
    public string CaptureDisplay { get; set; } = "";     // DXGI device name, blank = primary display
    public int CaptureFps { get; set; } = 30;            // how often the display is sampled
    public int CaptureSmoothingMs { get; set; } = 400;   // temporal smoothing of the captured colour

    // Music reaction — a group set to "breathe" has its intensity ridden by the bass in the audio
    // feed. Only the intensity: the colour still comes from that group's own source.

    /// <summary>
    /// Which audio input to listen to. Blank means the default playback device, captured as
    /// loopback. Otherwise "render:{id}" for another playback device (loopback again) or
    /// "capture:{id}" for a real input — a line feed from the sound desk, which is what you want.
    /// </summary>
    public string AudioDeviceId { get; set; } = "";

    /// <summary>
    /// Level in dBFS below which the feed counts as silence and the lights settle. This is the
    /// control that keeps talking near an open mic from moving the lights, so it's worth setting
    /// by eye against the meter in Settings.
    /// </summary>
    public int AudioGateDb { get; set; } = -48;

    /// <summary>
    /// How long the level takes to fall back after the music drops, in ms. Higher is calmer. The
    /// rise is deliberately much faster — see <see cref="Services.AudioLevelSource"/>.
    /// </summary>
    public int AudioResponseMs { get; set; } = 400;

    // Colour behaviour
    public int FadeMs { get; set; } = 800;
    public int BrightnessPercent { get; set; } = 100;
    public int SaturationBoostPercent { get; set; } = 25;
    public bool FullBrightnessColours { get; set; } = true; // normalise so lights are always at full intensity
    public string Fallback { get; set; } = "keep";     // keep | off | white — used when a slide has no real colour

    // Remote control — lets Bitfocus Companion (Stream Deck) recall scenes and toggle output over
    // HTTP. Off by default: when on, the listener is reachable from the whole local network.
    public bool RemoteEnabled { get; set; } = false;
    public int RemotePort { get; set; } = 7373;

    /// <summary>Shared secret required as ?key= or an X-Api-Key header. Blank disables the check.</summary>
    public string RemoteKey { get; set; } = "";

    // App behaviour
    public bool StartEnabled { get; set; } = false;
    public bool AutoCheckForUpdates { get; set; } = true; // check GitHub for a newer version on startup
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public string[] FavouriteColours { get; set; } = new[]
    {
        "#FF0000",
        "#00FF00",
        "#0000FF",
        "#FFFF00",
        "#FF00FF",
        "#00FFFF",
        "#FFFFFF",
        "#FF7F00",
        "#7F00FF",
        "#808080"
    };

    /// <summary>Set from the --settings command-line argument to use a custom settings file.</summary>
    public static string? OverridePath { get; set; }

    // WindowLeft/Top default to NaN, which JSON can only carry as the literal string "NaN"
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlideColours");
    private static string FilePath => OverridePath ?? Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            // corrupt settings file — fall back to defaults
            settings = new AppSettings();
        }
        settings.MigrateRig();
        return settings;
    }

    /// <summary>
    /// Normalises the rig, and seeds it on first run. Settings written before groups existed carry a
    /// single patch in <see cref="StartChannel"/>/<see cref="HasMasterDimmer"/>, so that becomes one
    /// fixture in one group keeping whichever source the app was last left in — an existing install
    /// comes back up behaving exactly as it did. An empty rig is only filled in when both lists are
    /// empty, so deleting every light in the editor stays deleted.
    /// </summary>
    public void MigrateRig()
    {
        Fixtures ??= new List<Fixture>();
        Groups ??= new List<LightGroup>();

        if (Fixtures.Count == 0 && Groups.Count == 0)
        {
            var fixture = new Fixture
            {
                Name = "Stage wash",
                StartChannel = Math.Clamp(StartChannel, 1, 512),
                HasMasterDimmer = HasMasterDimmer,
            };
            Fixtures.Add(fixture);
            Groups.Add(new LightGroup
            {
                Name = "All lights",
                FixtureIds = new List<string> { fixture.Id },
                Source = ColourSource == "screen" ? ColourSources.Screen : ColourSources.Slide,
            });
        }

        foreach (var group in Groups)
        {
            group.FixtureIds ??= new List<string>();
            group.Source = ColourSources.Normalise(group.Source);
            group.Pulse = PulseModes.Normalise(group.Pulse);
            group.PulseDepthPercent = Math.Clamp(group.PulseDepthPercent, 0, 100);
        }

        if (!Groups.Any(g => g.Id == ActiveGroupId))
            ActiveGroupId = Groups.Count > 0 ? Groups[0].Id : "";

        // A startup group that has since been deleted reverts to "last used" rather than pinning
        // the app to a group that no longer exists.
        if (StartupGroupId.Length > 0 && !Groups.Any(g => g.Id == StartupGroupId))
            StartupGroupId = "";

        Scenes ??= new List<Scene>();
        foreach (var scene in Scenes)
        {
            scene.Groups ??= new List<SceneGroupState>();
            scene.Groups.RemoveAll(entry => !Groups.Any(g => g.Id == entry.GroupId));
            foreach (var entry in scene.Groups)
            {
                entry.Source = ColourSources.Normalise(entry.Source);
                entry.Pulse = PulseModes.Normalise(entry.Pulse);
                entry.PulseDepthPercent = Math.Clamp(entry.PulseDepthPercent, 0, 100);
            }
        }

        if (StartupSceneId.Length > 0 && !Scenes.Any(s => s.Id == StartupSceneId))
            StartupSceneId = "";
    }

    /// <summary>The scene to recall on launch, or null when none is pinned or it no longer exists.</summary>
    [JsonIgnore]
    public Scene? StartupScene =>
        StartupSceneId.Length > 0 ? Scenes.FirstOrDefault(s => s.Id == StartupSceneId) : null;

    /// <summary>The group to select on launch: the pinned one if it's still around, else the last used.</summary>
    public string ResolveStartupGroupId() =>
        StartupGroupId.Length > 0 && Groups.Any(g => g.Id == StartupGroupId)
            ? StartupGroupId
            : ActiveGroupId;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // non-fatal: app still works without persisted settings
        }
    }

    /// <summary>Where settings are being read from and written to, for the Settings window to show.</summary>
    public static string CurrentFilePath => FilePath;

    /// <summary>
    /// Writes a copy to any path, for backing settings up or moving them to another machine.
    /// Unlike <see cref="Save"/> this reports failures, since the user asked for it explicitly.
    /// </summary>
    public void SaveTo(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));

    /// <summary>
    /// Reads settings from any path. Throws if the file isn't readable or isn't settings JSON —
    /// the caller reports that rather than silently keeping the old values.
    /// </summary>
    public static AppSettings LoadFrom(string path)
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOpts)
            ?? throw new InvalidDataException("That file doesn't contain any Slide Colours settings.");
        loaded.MigrateRig();
        return loaded;
    }

    /// <summary>
    /// Copies every stored value out of <paramref name="other"/> into this instance.
    ///
    /// The engine, the ProPresenter client and the screen capture all hold a reference to one
    /// AppSettings for the life of the app, so loading a file has to overwrite this object rather
    /// than swap in a new one. Reflection over the read/write properties keeps that automatic:
    /// a setting added later is copied without anyone remembering to update this method.
    /// </summary>
    public void CopyFrom(AppSettings other)
    {
        foreach (var property in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.CanRead && property.CanWrite)
                property.SetValue(this, property.GetValue(other));
    }
}
