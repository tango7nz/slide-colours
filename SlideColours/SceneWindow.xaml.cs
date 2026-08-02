using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SlideColours.Models;
using SlideColours.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace SlideColours;

/// <summary>
/// Editor for saved looks. Edits are made on copies so Cancel really cancels; Save writes them back.
/// Nothing here changes what the lights are doing — recalling a scene is done from the main window.
/// </summary>
public partial class SceneWindow : Window
{
    private readonly AppSettings _settings;
    private readonly LightingEngine _engine;
    private readonly List<Scene> _scenes;

    /// <summary>One editable row: the group, the source picked for it, and its manual colour.</summary>
    private sealed class GroupRow
    {
        public required string GroupId { get; init; }
        public required WpfComboBox SourceBox { get; init; }
        public required WpfButton ColourButton { get; init; }
        public WpfColor Colour { get; set; }
    }

    private readonly List<GroupRow> _rows = new();
    private bool _suspend;

    /// <summary>
    /// The scene the rows currently belong to. The rows are the live edit surface, so they have to
    /// be written back into this before the selection moves on — otherwise clicking another scene
    /// silently discards whatever was just changed.
    /// </summary>
    private Scene? _editing;

    public SceneWindow(AppSettings settings, LightingEngine engine)
    {
        _settings = settings;
        _engine = engine;
        _scenes = settings.Scenes.Select(s => s.Clone()).ToList();

        InitializeComponent();

        BuildGroupRows();
        RebuildSceneList(0);
    }

    // ------------------------------------------------------------ scene list

    private Scene? SelectedScene => (SceneList.SelectedItem as ListBoxItem)?.Tag as Scene;

    private void RebuildSceneList(int select)
    {
        _suspend = true;
        SceneList.Items.Clear();
        foreach (var scene in _scenes)
            SceneList.Items.Add(new ListBoxItem { Content = scene.DisplayName, Tag = scene });
        _suspend = false;

        SceneList.SelectedIndex = _scenes.Count == 0 ? -1 : Math.Clamp(select, 0, _scenes.Count - 1);
        LoadSceneDetail();
    }

    private void SceneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend)
            return;
        CommitEditing();
        LoadSceneDetail();
    }

    private void LoadSceneDetail()
    {
        _suspend = true;
        var scene = SelectedScene;

        SceneDetail.IsEnabled = scene != null;
        RemoveSceneButton.IsEnabled = scene != null;
        CaptureButton.IsEnabled = scene != null;
        GroupRows.IsEnabled = scene != null;
        SceneNameBox.Text = scene?.Name ?? "";

        if (scene != null)
        {
            foreach (var row in _rows)
            {
                // A group added since this scene was saved isn't in it; seed the row from what that
                // group is doing now, so saving gives the scene complete coverage.
                var entry = scene.For(row.GroupId);
                var group = _settings.Groups.FirstOrDefault(g => g.Id == row.GroupId);
                string source = entry?.Source ?? group?.Source ?? ColourSources.Slide;
                string colour = entry?.ManualColour ?? group?.ManualColour ?? "#FFFFFF";

                SelectByTag(row.SourceBox, source);
                SetRowColour(row, ParseColour(colour));
            }
        }

        _suspend = false;
        _editing = scene;
        RefreshRowStates();
        UpdateHint();
    }

    private void SceneName_Changed(object sender, RoutedEventArgs e)
    {
        if (_suspend || SelectedScene is not { } scene)
            return;

        scene.Name = SceneNameBox.Text;
        if (SceneList.SelectedItem is ListBoxItem item)
            item.Content = scene.DisplayName;
    }

    private void AddScene_Click(object sender, RoutedEventArgs e)
    {
        CommitEditing();

        // Start from the live stage: setting the look first and then saving it is how an operator
        // actually works, and it beats filling in a table from scratch.
        _scenes.Add(new Scene
        {
            Name = $"Scene {_scenes.Count + 1}",
            Groups = _engine.CaptureScene(),
        });
        RebuildSceneList(_scenes.Count - 1);

        SceneNameBox.Focus();
        SceneNameBox.SelectAll();
    }

    private void RemoveScene_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScene is not { } scene)
            return;

        int index = SceneList.SelectedIndex;
        _scenes.Remove(scene);
        _editing = null;
        RebuildSceneList(index);
    }

    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScene is not { } scene)
            return;

        scene.Groups = _engine.CaptureScene();
        LoadSceneDetail();
    }

    // ------------------------------------------------------------ group rows

    /// <summary>Builds one row per group. The rows persist; only their values change per scene.</summary>
    private void BuildGroupRows()
    {
        GroupRows.Children.Clear();
        _rows.Clear();

        if (_settings.Groups.Count == 0)
        {
            GroupRows.Children.Add(new TextBlock
            {
                Text = "No groups yet — add some in Settings → Lights & groups first.",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var group in _settings.Groups)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

            var label = new TextBlock
            {
                Text = group.DisplayName,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(2, 0, 8, 0),
            };
            Grid.SetColumn(label, 0);

            var sourceBox = new WpfComboBox { Margin = new Thickness(0, 2, 0, 2) };
            foreach (var source in new[]
                     { ColourSources.Slide, ColourSources.Screen, ColourSources.Manual, ColourSources.Off })
            {
                sourceBox.Items.Add(new WpfComboBoxItem { Tag = source, Content = ColourSources.Label(source) });
            }
            sourceBox.SelectionChanged += Row_SourceChanged;
            Grid.SetColumn(sourceBox, 1);

            var colourButton = new WpfButton
            {
                Content = "Colour…",
                Margin = new Thickness(8, 2, 0, 2),
                Padding = new Thickness(0, 3, 0, 3),
                Foreground = Brushes.White,
            };
            colourButton.Click += Row_ColourClick;
            Grid.SetColumn(colourButton, 2);

            grid.Children.Add(label);
            grid.Children.Add(sourceBox);
            grid.Children.Add(colourButton);
            GroupRows.Children.Add(grid);

            var row = new GroupRow { GroupId = group.Id, SourceBox = sourceBox, ColourButton = colourButton };
            sourceBox.Tag = row;
            colourButton.Tag = row;
            _rows.Add(row);
        }
    }

    private void Row_SourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend)
            return;
        RefreshRowStates();
        UpdateHint();
    }

    private void Row_ColourClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: GroupRow row })
            return;

        var picker = new ColourPickerWindow(row.Colour) { Owner = this };
        if (picker.ShowDialog() == true)
            SetRowColour(row, picker.SelectedColour);
    }

    private void SetRowColour(GroupRow row, WpfColor colour)
    {
        row.Colour = colour;
        row.ColourButton.Background = new SolidColorBrush(colour);
        // Keep the caption readable whatever the swatch colour is.
        double luma = (0.299 * colour.R + 0.587 * colour.G + 0.114 * colour.B) / 255.0;
        row.ColourButton.Foreground = luma > 0.6 ? Brushes.Black : Brushes.White;
    }

    /// <summary>The colour button only means anything for a group holding a manual colour.</summary>
    private void RefreshRowStates()
    {
        foreach (var row in _rows)
            row.ColourButton.IsEnabled = SelectedTag(row.SourceBox) == ColourSources.Manual;
    }

    private void UpdateHint()
    {
        if (SelectedScene == null)
        {
            SceneHintText.Text = "";
            return;
        }

        int screen = _rows.Count(r => SelectedTag(r.SourceBox) == ColourSources.Screen);
        int manual = _rows.Count(r => SelectedTag(r.SourceBox) == ColourSources.Manual);
        int off = _rows.Count(r => SelectedTag(r.SourceBox) == ColourSources.Off);
        int slide = _rows.Count - screen - manual - off;

        var parts = new List<string>();
        if (slide > 0) parts.Add($"{slide} following slides");
        if (screen > 0) parts.Add($"{screen} following the screen");
        if (manual > 0) parts.Add($"{manual} on a fixed colour");
        if (off > 0) parts.Add($"{off} off");
        SceneHintText.Text = string.Join(", ", parts);
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
        return Colors.White;
    }

    /// <summary>Writes the on-screen rows back into the scene they were loaded from.</summary>
    private void CommitEditing()
    {
        if (_editing is not { } scene || !_scenes.Contains(scene))
            return;

        scene.Groups = _rows.Select(row => new SceneGroupState
        {
            GroupId = row.GroupId,
            Source = SelectedTag(row.SourceBox),
            ManualColour = $"#{row.Colour.R:X2}{row.Colour.G:X2}{row.Colour.B:X2}",
        }).ToList();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitEditing();

        for (int i = 0; i < _scenes.Count; i++)
            if (string.IsNullOrWhiteSpace(_scenes[i].Name))
                _scenes[i].Name = $"Scene {i + 1}";

        _settings.Scenes = _scenes;
        _settings.MigrateRig();
        _settings.Save();

        DialogResult = true;
    }
}
