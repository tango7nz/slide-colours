using System.Text.Json.Serialization;

namespace SlideColours.Models;

/// <summary>
/// One light in the rig: a name the operator recognises ("Left Front") and the DMX channel its
/// colour starts at. Fixtures are addressed by <see cref="Id"/> so a group keeps pointing at the
/// right light after it is renamed or re-patched.
/// </summary>
public class Fixture
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>First DMX channel of the fixture, 1-512. Red, green and blue follow in order.</summary>
    public int StartChannel { get; set; } = 1;

    /// <summary>True when the channel layout is Dimmer,R,G,B rather than R,G,B.</summary>
    public bool HasMasterDimmer { get; set; }

    /// <summary>Channels this fixture occupies, for overlap warnings in the editor.</summary>
    [JsonIgnore]
    public int ChannelCount => HasMasterDimmer ? 4 : 3;

    public Fixture Clone() => new()
    {
        Id = Id,
        Name = Name,
        StartChannel = StartChannel,
        HasMasterDimmer = HasMasterDimmer,
    };

    public string Describe()
    {
        string name = string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name;
        int last = StartChannel + ChannelCount - 1;
        return $"{name}  —  ch {StartChannel}-{last}{(HasMasterDimmer ? " (dimmer)" : "")}";
    }
}
