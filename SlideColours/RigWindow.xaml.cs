using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SlideColours.Models;
using SlideColours.Services;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace SlideColours;

/// <summary>
/// Editor for the rig: the individual lights and the groups built from them. Edits are made on
/// copies so Cancel really cancels; Save writes them back and reloads the engine.
/// </summary>
public partial class RigWindow : Window
{
    private readonly AppSettings _settings;
    private readonly LightingEngine _engine;
    private readonly List<Fixture> _fixtures;
    private readonly List<LightGroup> _groups;

    /// <summary>Set while controls are being populated, so the change handlers don't write back.</summary>
    private bool _suspend;

    public RigWindow(AppSettings settings, LightingEngine engine)
    {
        _settings = settings;
        _engine = engine;
        _fixtures = settings.Fixtures.Select(f => f.Clone()).ToList();
        _groups = settings.Groups.Select(g => g.Clone()).ToList();

        InitializeComponent();

        RebuildFixtureList(0);
        RebuildGroupList(IndexOfActiveGroup());
        RefreshWarnings();

        // Don't leave a light flashing on stage after this window has gone.
        Closed += (_, _) => _engine.StopPatchTest();
    }

    private int IndexOfActiveGroup()
    {
        int index = _groups.FindIndex(g => g.Id == _settings.ActiveGroupId);
        return index < 0 ? 0 : index;
    }

    // ---------------------------------------------------------------- lights

    private Fixture? SelectedFixture => (FixtureList.SelectedItem as ListBoxItem)?.Tag as Fixture;

    private void RebuildFixtureList(int select)
    {
        _suspend = true;
        FixtureList.Items.Clear();
        foreach (var fixture in _fixtures)
            FixtureList.Items.Add(new ListBoxItem { Content = fixture.Describe(), Tag = fixture });
        _suspend = false;

        FixtureList.SelectedIndex = _fixtures.Count == 0 ? -1 : Math.Clamp(select, 0, _fixtures.Count - 1);
        LoadFixtureDetail();
    }

    private void LoadFixtureDetail()
    {
        _suspend = true;
        var fixture = SelectedFixture;

        FixtureDetail.IsEnabled = fixture != null;
        RemoveFixtureButton.IsEnabled = fixture != null;
        FlashFixtureButton.IsEnabled = fixture != null;
        FixtureNameBox.Text = fixture?.Name ?? "";
        FixtureChannelBox.Text = fixture?.StartChannel.ToString() ?? "";
        FixtureDimmerCheck.IsChecked = fixture?.HasMasterDimmer == true;

        _suspend = false;
    }

    private void FixtureList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suspend)
            LoadFixtureDetail();
    }

    private void FixtureField_Changed(object sender, RoutedEventArgs e)
    {
        if (_suspend || SelectedFixture is not { } fixture)
            return;

        fixture.Name = FixtureNameBox.Text;
        // Only accept a parsable channel — mid-typing the box is briefly empty or out of range,
        // and overwriting the text there would fight the user.
        if (int.TryParse(FixtureChannelBox.Text.Trim(), out int channel) && channel is >= 1 and <= 512)
            fixture.StartChannel = channel;
        fixture.HasMasterDimmer = FixtureDimmerCheck.IsChecked == true;

        // Relabel in place rather than rebuilding the list, which would steal focus from the caret.
        if (FixtureList.SelectedItem is ListBoxItem item)
            item.Content = fixture.Describe();
        RelabelMembership();
        RefreshWarnings();
    }

    private void AddFixture_Click(object sender, RoutedEventArgs e)
    {
        _fixtures.Add(new Fixture
        {
            Name = $"Light {_fixtures.Count + 1}",
            StartChannel = NextFreeChannel(),
        });
        RebuildFixtureList(_fixtures.Count - 1);
        RebuildMembership();
        RefreshWarnings();

        FixtureNameBox.Focus();
        FixtureNameBox.SelectAll();
    }

    /// <summary>First channel past everything already patched, so added lights don't overlap by default.</summary>
    private int NextFreeChannel()
    {
        int next = _fixtures.Count == 0
            ? 1
            : _fixtures.Max(f => f.StartChannel + f.ChannelCount);
        return Math.Clamp(next, 1, 512);
    }

    /// <summary>
    /// Identifies one light on stage. The engine is handed this window's working copy, not the
    /// saved fixture, so you can type a channel and check it before committing — which is the
    /// moment you actually want to know whether you got it right.
    /// </summary>
    private void FlashFixture_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFixture is { } fixture)
            _engine.RunPatchTest(new[] { fixture });
    }

    private void RemoveFixture_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFixture is not { } fixture)
            return;

        int index = FixtureList.SelectedIndex;
        _fixtures.Remove(fixture);
        foreach (var group in _groups)
            group.FixtureIds.Remove(fixture.Id);

        RebuildFixtureList(index);
        RebuildMembership();
        RefreshWarnings();
    }

    // ---------------------------------------------------------------- groups

    private LightGroup? SelectedGroup => (GroupList.SelectedItem as ListBoxItem)?.Tag as LightGroup;

    private void RebuildGroupList(int select)
    {
        _suspend = true;
        GroupList.Items.Clear();
        foreach (var group in _groups)
            GroupList.Items.Add(new ListBoxItem { Content = DescribeGroup(group), Tag = group });
        _suspend = false;

        GroupList.SelectedIndex = _groups.Count == 0 ? -1 : Math.Clamp(select, 0, _groups.Count - 1);
        LoadGroupDetail();
    }

    private string DescribeGroup(LightGroup group)
    {
        int count = group.FixtureIds.Count(id => _fixtures.Any(f => f.Id == id));
        string lights = count == 1 ? "1 light" : $"{count} lights";
        return $"{group.DisplayName}  —  {ColourSources.Label(group.Source)}, {lights}";
    }

    private void LoadGroupDetail()
    {
        _suspend = true;
        var group = SelectedGroup;

        GroupDetail.IsEnabled = group != null;
        RemoveGroupButton.IsEnabled = group != null;
        GroupNameBox.Text = group?.Name ?? "";
        SelectByTag(GroupSourceBox, group?.Source ?? ColourSources.Slide);

        _suspend = false;

        RebuildMembership();
        UpdateGroupHint();
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suspend)
            LoadGroupDetail();
    }

    private void GroupField_Changed(object sender, RoutedEventArgs e)
    {
        if (_suspend || SelectedGroup is not { } group)
            return;

        group.Name = GroupNameBox.Text;
        group.Source = SelectedTag(GroupSourceBox);

        if (GroupList.SelectedItem is ListBoxItem item)
            item.Content = DescribeGroup(group);
        UpdateGroupHint();
        RefreshWarnings();
    }

    private void UpdateGroupHint()
    {
        GroupHintText.Text = SelectedGroup?.Source switch
        {
            ColourSources.Screen => "Samples the display chosen in Settings, continuously.",
            ColourSources.Manual => "Holds a fixed colour. Select this group in the main window and click a swatch to change it.",
            ColourSources.Off => "These lights are held at zero while other groups keep running.",
            ColourSources.Slide => "Takes the dominant colour of each ProPresenter slide as it goes live.",
            _ => "",
        };
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        _groups.Add(new LightGroup { Name = $"Group {_groups.Count + 1}" });
        RebuildGroupList(_groups.Count - 1);
        RefreshWarnings();

        GroupNameBox.Focus();
        GroupNameBox.SelectAll();
    }

    private void RemoveGroup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } group)
            return;

        int index = GroupList.SelectedIndex;
        _groups.Remove(group);
        RebuildGroupList(index);
        RefreshWarnings();
    }

    // ------------------------------------------------------------ membership

    private void RebuildMembership()
    {
        _suspend = true;
        MembershipPanel.Children.Clear();

        var group = SelectedGroup;
        if (group != null)
        {
            if (_fixtures.Count == 0)
            {
                MembershipPanel.Children.Add(new TextBlock
                {
                    Text = "Add a light on the left first.",
                    Foreground = Brushes.Gray,
                });
            }
            else
            {
                foreach (var fixture in _fixtures)
                {
                    var check = new WpfCheckBox
                    {
                        Content = fixture.Describe(),
                        Tag = fixture.Id,
                        Margin = new Thickness(0, 2, 0, 2),
                        IsChecked = group.FixtureIds.Contains(fixture.Id),
                    };
                    check.Checked += Membership_Changed;
                    check.Unchecked += Membership_Changed;
                    MembershipPanel.Children.Add(check);
                }
            }
        }

        _suspend = false;
    }

    /// <summary>Keeps the checkbox captions in step while a light is being renamed or re-patched.</summary>
    private void RelabelMembership()
    {
        foreach (var check in MembershipPanel.Children.OfType<WpfCheckBox>())
        {
            var fixture = _fixtures.FirstOrDefault(f => f.Id == (string)check.Tag);
            if (fixture != null)
                check.Content = fixture.Describe();
        }
    }

    private void Membership_Changed(object sender, RoutedEventArgs e)
    {
        if (_suspend || SelectedGroup is not { } group)
            return;

        group.FixtureIds = MembershipPanel.Children.OfType<WpfCheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => (string)c.Tag)
            .ToList();

        if (GroupList.SelectedItem is ListBoxItem item)
            item.Content = DescribeGroup(group);
        RefreshWarnings();
    }

    // -------------------------------------------------------------- warnings

    /// <summary>
    /// Non-blocking advice. Overlapping patches and orphaned lights are both legal — the frame
    /// merges highest-takes-precedence — but they're nearly always a mistake worth pointing at.
    /// </summary>
    private void RefreshWarnings()
    {
        var messages = new List<string>();

        var overlaps = _fixtures
            .SelectMany(f => Enumerable.Range(f.StartChannel, f.ChannelCount).Select(ch => (ch, f)))
            .GroupBy(x => x.ch)
            .Where(g => g.Select(x => x.f).Distinct().Count() > 1)
            .SelectMany(g => g.Select(x => x.f))
            .Distinct()
            .ToList();
        if (overlaps.Count > 0)
        {
            messages.Add("These lights share DMX channels and will move together: "
                       + string.Join(", ", overlaps.Select(f => f.Name)));
        }

        if (_fixtures.Any(f => f.StartChannel + f.ChannelCount - 1 > 512))
        {
            messages.Add("A light runs past channel 512 — its last channels won't be sent.");
        }

        var orphans = _fixtures.Where(f => !_groups.Any(g => g.FixtureIds.Contains(f.Id))).ToList();
        if (orphans.Count > 0)
        {
            messages.Add("Not in any group, so they'll stay dark: "
                       + string.Join(", ", orphans.Select(f => f.Name)));
        }

        WarningText.Text = string.Join("\n", messages);
        WarningText.Visibility = messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ save

    private static void SelectByTag(WpfComboBox box, string tag)
    {
        foreach (WpfComboBoxItem item in box.Items)
        {
            if ((string)item.Tag == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private static string SelectedTag(WpfComboBox box) =>
        (box.SelectedItem as WpfComboBoxItem)?.Tag as string ?? ColourSources.Slide;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Blank names make the group editor and the main window's picker unusable, so fill them in
        // rather than refusing to save.
        for (int i = 0; i < _fixtures.Count; i++)
            if (string.IsNullOrWhiteSpace(_fixtures[i].Name))
                _fixtures[i].Name = $"Light {i + 1}";
        for (int i = 0; i < _groups.Count; i++)
            if (string.IsNullOrWhiteSpace(_groups[i].Name))
                _groups[i].Name = $"Group {i + 1}";

        _settings.Fixtures = _fixtures;
        _settings.Groups = _groups;
        _settings.MigrateRig();
        _settings.Save();
        _engine.ReloadRig();

        DialogResult = true;
    }
}
