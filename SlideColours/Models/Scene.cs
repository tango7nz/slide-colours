using System.Text.Json.Serialization;

namespace SlideColours.Models;

/// <summary>
/// A named look for the whole stage: what every group should be doing, saved so it can be recalled
/// in one click. "Pre-service", "Worship", "Message".
///
/// A scene is a snapshot, deliberately separate from the groups themselves. Changing a colour live
/// during a service edits the group, never the scene — so the look a scene restores is the one that
/// was explicitly saved, not whatever last Sunday happened to end on.
/// </summary>
public class Scene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>One entry per group. Groups added since the scene was saved simply aren't listed.</summary>
    public List<SceneGroupState> Groups { get; set; } = new();

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "(unnamed scene)" : Name;

    public Scene Clone() => new()
    {
        Id = Id,
        Name = Name,
        Groups = Groups.Select(g => g.Clone()).ToList(),
    };

    public SceneGroupState? For(string groupId) => Groups.FirstOrDefault(g => g.GroupId == groupId);
}

/// <summary>What one group is set to within a scene.</summary>
public class SceneGroupState
{
    public string GroupId { get; set; } = "";

    /// <summary>slide | screen | manual | off — see <see cref="ColourSources"/>.</summary>
    public string Source { get; set; } = ColourSources.Slide;

    /// <summary>The colour to hold when <see cref="Source"/> is "manual".</summary>
    public string ManualColour { get; set; } = "#FFFFFF";

    public SceneGroupState Clone() => new()
    {
        GroupId = GroupId,
        Source = Source,
        ManualColour = ManualColour,
    };

    /// <summary>True when a group is currently doing exactly what this entry describes.</summary>
    public bool Matches(LightGroup group) =>
        group.Source == Source &&
        (Source != ColourSources.Manual ||
         string.Equals(group.ManualColour, ManualColour, StringComparison.OrdinalIgnoreCase));
}
