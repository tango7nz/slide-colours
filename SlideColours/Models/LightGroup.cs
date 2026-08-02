using System.Text.Json.Serialization;

namespace SlideColours.Models;

/// <summary>
/// A named set of fixtures that share one colour and one source. Groups run independently and all
/// at once, so the front wash can follow the screen while a side wash holds a fixed colour.
/// </summary>
public class LightGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary><see cref="Fixture.Id"/> of each member. Ids that no longer exist are ignored.</summary>
    public List<string> FixtureIds { get; set; } = new();

    /// <summary>slide | screen | manual | off — see <see cref="ColourSources"/>.</summary>
    public string Source { get; set; } = ColourSources.Slide;

    /// <summary>The colour held while <see cref="Source"/> is "manual".</summary>
    public string ManualColour { get; set; } = "#FFFFFF";

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "(unnamed group)" : Name;

    public LightGroup Clone() => new()
    {
        Id = Id,
        Name = Name,
        FixtureIds = new List<string>(FixtureIds),
        Source = Source,
        ManualColour = ManualColour,
    };
}

/// <summary>The values <see cref="LightGroup.Source"/> can take, and their labels.</summary>
public static class ColourSources
{
    public const string Slide = "slide";
    public const string Screen = "screen";
    public const string Manual = "manual";
    public const string Off = "off";

    public static string Label(string source) => source switch
    {
        Screen => "Follow screen",
        Manual => "Manual colour",
        Off => "Off",
        _ => "Follow slide",
    };

    /// <summary>Maps anything unrecognised (an older or hand-edited settings file) back to "slide".</summary>
    public static string Normalise(string? source) => source switch
    {
        Screen => Screen,
        Manual => Manual,
        Off => Off,
        _ => Slide,
    };
}
