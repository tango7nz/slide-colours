using System.IO;
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
    public int StartChannel { get; set; } = 1;         // 1-512, first channel of the fixture
    public bool HasMasterDimmer { get; set; } = false; // true = layout is Dimmer,R,G,B; false = R,G,B
    public string ComPort { get; set; } = "COM3";      // Enttec DMX USB Pro only

    // Colour behaviour
    public int FadeMs { get; set; } = 800;
    public int BrightnessPercent { get; set; } = 100;
    public int SaturationBoostPercent { get; set; } = 25;
    public bool FullBrightnessColours { get; set; } = true; // normalise so lights are always at full intensity
    public string Fallback { get; set; } = "keep";     // keep | off | white — used when a slide has no real colour

    // App behaviour
    public bool StartEnabled { get; set; } = false;
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
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new AppSettings();
        }
        catch
        {
            // corrupt settings file — fall back to defaults
        }
        return new AppSettings();
    }

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
}
