using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SlideColours.Models;
using SlideColours.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfButton = System.Windows.Controls.Button;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace SlideColours;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly LightingEngine _engine;
    private readonly ProPresenterClient _client;
    private readonly ScreenColourSource _screen;
    private readonly AudioLevelSource _audio;
    private readonly UpdateService _update;
    private readonly RemoteControlServer _remote;

    private bool _connected;
    private string? _captureStatus;
    private string? _audioStatus;

    /// <summary>Set while the group picker is being repopulated, so its handler doesn't fire back.</summary>
    private bool _suspend;

    private static readonly WpfBrush ModeActiveBrush = new SolidColorBrush(WpfColor.FromRgb(0x3A, 0x6F, 0xF0));
    private static readonly WpfBrush ModeMutedBrush = new SolidColorBrush(WpfColor.FromRgb(0xB8, 0xB8, 0xC0));
    private static readonly WpfBrush ModeFaintBorderBrush = new SolidColorBrush(WpfColor.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    public MainWindow(AppSettings settings, LightingEngine engine, ProPresenterClient client,
        ScreenColourSource screen, AudioLevelSource audio, UpdateService update, RemoteControlServer remote)
    {
        _settings = settings;
        _engine = engine;
        _client = client;
        _screen = screen;
        _audio = audio;
        _update = update;
        _remote = remote;

        InitializeComponent();
        RestorePosition();
        BuildFavouriteButtons();

        // Show the settings-cog dot if a startup check has already found an update, and keep
        // watching in case the check completes after the window is up.
        if (_update.Available != null)
            UpdateBadge.Visibility = Visibility.Visible;
        _update.UpdateAvailable += _ => Dispatcher.BeginInvoke(() =>
            UpdateBadge.Visibility = Visibility.Visible);

        EnableToggle.IsChecked = _engine.Enabled;
        _connected = _client.Connected;
        _captureStatus = _screen.Status;
        _audioStatus = _audio.Status;
        RebuildGroupPicker();
        RefreshScenePicker();

        _engine.GroupsChanged += () => Dispatcher.BeginInvoke(() =>
        {
            RebuildGroupPicker();
            RefreshScenePicker();
        });

        _client.ConnectionChanged += connected => Dispatcher.BeginInvoke(() =>
        {
            _connected = connected;
            RefreshModeButtons();
        });

        _screen.StatusChanged += status => Dispatcher.BeginInvoke(() =>
        {
            _captureStatus = status;
            RefreshModeButtons();
        });

        _audio.StatusChanged += status => Dispatcher.BeginInvoke(() =>
        {
            _audioStatus = status;
            RefreshModeButtons();
        });

        // Companion can switch the output too, and the toggle has to follow. Writing IsChecked
        // re-enters EnableToggle_Changed, but that only writes back the value the engine already
        // holds, which is a no-op rather than a loop.
        _engine.EnabledChanged += on => Dispatcher.BeginInvoke(() =>
        {
            EnableToggle.IsChecked = on;
        });

        _engine.OutputChanged += (colour, sending) => Dispatcher.BeginInvoke(() =>
        {
            ColourSwatch.Fill = new SolidColorBrush(colour);
            ColourSwatch.Opacity = sending ? 1.0 : 0.35;
        });

        _engine.OutputError += error => Dispatcher.BeginInvoke(() =>
        {
            ColourSwatch.ToolTip = error ?? "Current output colour";
        });

        // The engine has been running since startup, so ask it to re-announce where it got to.
        _engine.ResendOutputState();

        // The pill sizes to its content, and that content settles over several layout passes — a
        // one-shot clamp at Loaded can run against a width that is still growing and leave the
        // right-hand end off-screen. Re-clamping on every size change is cheap and can't loop,
        // since it only ever moves the window.
        SizeChanged += (_, _) => ClampToScreen();
    }

    private void ClampToScreen()
    {
        var area = SystemParameters.WorkArea;
        if (Left + ActualWidth > area.Right) Left = area.Right - ActualWidth - 12;
        if (Top + ActualHeight > area.Bottom) Top = area.Bottom - ActualHeight - 12;
        if (Left < area.Left) Left = area.Left + 12;
        if (Top < area.Top) Top = area.Top + 12;
    }

    private void BuildFavouriteButtons()
    {
        FavouritePanel.Children.Clear();
        for (int i = 0; i < 10; i++)
        {
            var colour = ParseColour(_settings.FavouriteColours[Math.Clamp(i, 0, _settings.FavouriteColours.Length - 1)]);
            var button = new WpfButton
            {
                Style = (Style)FindResource("SwatchButton"),
                Width = 18,
                Height = 18,
                Margin = new Thickness(2),
                BorderBrush = new SolidColorBrush(WpfColor.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(colour),
                ToolTip = "Left-click to use\nRight-click to edit this colour",
                Tag = i,
            };
            button.Click += FavouriteButton_Click;
            button.MouseRightButtonUp += FavouriteButton_RightClick;
            FavouritePanel.Children.Add(button);
        }
    }

    private void RestorePosition()
    {
        if (double.IsNaN(_settings.WindowLeft) || double.IsNaN(_settings.WindowTop))
        {
            Left = SystemParameters.WorkArea.Right - 260;
            Top = SystemParameters.WorkArea.Top + 20;
            return;
        }

        // only restore if still on a visible screen
        bool visible = _settings.WindowLeft >= SystemParameters.VirtualScreenLeft - 50
                    && _settings.WindowTop >= SystemParameters.VirtualScreenTop - 20
                    && _settings.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50
                    && _settings.WindowTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 30;
        if (visible)
        {
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.Save();
        }
    }

    private void EnableToggle_Changed(object sender, RoutedEventArgs e)
    {
        _engine.Enabled = EnableToggle.IsChecked == true;
    }

    /// <summary>
    /// Lists the saved scenes and highlights whichever one the stage currently matches. The first
    /// entry is a dash, shown whenever the live state doesn't match any scene — so the picker never
    /// claims a look is loaded after the operator has nudged something by hand.
    /// </summary>
    private void RefreshScenePicker()
    {
        _suspend = true;
        ScenePicker.Items.Clear();
        ScenePicker.Items.Add(new WpfComboBoxItem { Tag = "", Content = "—" });
        foreach (var scene in _settings.Scenes)
            ScenePicker.Items.Add(new WpfComboBoxItem { Tag = scene.Id, Content = scene.DisplayName });

        var live = _engine.ActiveScene();
        int index = live == null ? 0 : _settings.Scenes.FindIndex(s => s.Id == live.Id) + 1;
        ScenePicker.SelectedIndex = index;
        _suspend = false;

        bool any = _settings.Scenes.Count > 0;
        ScenePicker.IsEnabled = any;
        ScenePicker.ToolTip = !any
            ? "No scenes yet — create them in Settings → Scenes"
            : live != null
                ? $"The stage matches “{live.DisplayName}”"
                : "Changed by hand — pick a scene to restore a saved look";
    }

    private void ScenePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend)
            return;

        // Selecting the dash means "no scene"; there's nothing sensible to restore, so leave the
        // lights alone rather than blacking the stage out mid-service.
        if ((ScenePicker.SelectedItem as WpfComboBoxItem)?.Tag is not string id || id.Length == 0)
            return;

        if (_settings.Scenes.FirstOrDefault(s => s.Id == id) is { } scene)
            _engine.ApplyScene(scene);
    }

    /// <summary>The group the pill's controls act on, or null when the rig has no groups.</summary>
    private string? ActiveGroupId => (GroupPicker.SelectedItem as WpfComboBoxItem)?.Tag as string;

    private void RebuildGroupPicker()
    {
        _suspend = true;
        GroupPicker.Items.Clear();
        foreach (var group in _settings.Groups)
            GroupPicker.Items.Add(new WpfComboBoxItem { Tag = group.Id, Content = group.DisplayName });

        GroupPicker.IsEnabled = _settings.Groups.Count > 0;
        if (_settings.Groups.Count == 0)
        {
            GroupPicker.Items.Add(new WpfComboBoxItem { Tag = null, Content = "No groups" });
            GroupPicker.SelectedIndex = 0;
        }
        else
        {
            int index = _settings.Groups.FindIndex(g => g.Id == _engine.ActiveGroupId);
            GroupPicker.SelectedIndex = index < 0 ? 0 : index;
        }
        _suspend = false;

        // Selecting index 0 above may not raise SelectionChanged, so sync the engine explicitly.
        if (ActiveGroupId is { } id)
            _engine.ActiveGroupId = id;
        RefreshModeButtons();
    }

    private void GroupPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend || ActiveGroupId is not { } id)
            return;
        _engine.ActiveGroupId = id;
        RefreshModeButtons();
    }

    private void SetActiveGroupSource(string source)
    {
        if (ActiveGroupId is { } id)
            _engine.SetGroupSource(id, source);
    }

    private void FollowSlide_Click(object sender, RoutedEventArgs e) => SetActiveGroupSource(ColourSources.Slide);

    private void FollowScreen_Click(object sender, RoutedEventArgs e) => SetActiveGroupSource(ColourSources.Screen);

    private void GroupOff_Click(object sender, RoutedEventArgs e) => SetActiveGroupSource(ColourSources.Off);

    /// <summary>
    /// Flips the selected group between breathing and steady. A toggle rather than a mode button
    /// because breathing isn't a colour source — it sits on top of whichever one is selected.
    /// </summary>
    private void Breathe_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveGroupId is not { } id)
            return;

        bool breathing = _engine.PulseOf(id) == PulseModes.Breathe;
        _engine.SetGroupPulse(id, breathing ? PulseModes.Off : PulseModes.Breathe);
    }

    private void RefreshModeButtons()
    {
        string? groupId = ActiveGroupId;
        string source = groupId == null ? "" : _engine.SourceOf(groupId);
        string group = groupId == null
            ? ""
            : _settings.Groups.FirstOrDefault(g => g.Id == groupId)?.DisplayName ?? "";
        bool hasGroup = groupId != null;

        // Following slides is only possible while ProPresenter is connected. Screen capture needs
        // nothing but a display, so that button is always available.
        FollowSlideButton.IsEnabled = hasGroup && _connected;
        Highlight(FollowSlideButton, source == ColourSources.Slide);
        FollowSlideButton.ToolTip = !hasGroup
            ? "Add a group in Settings → Lights & groups"
            : !_connected
                ? $"ProPresenter not connected ({_settings.ProPresenterHost}:{_settings.ProPresenterPort}) — slide following unavailable"
                : source == ColourSources.Slide
                    ? $"{group} is following the live slide"
                    : $"Click to match {group} to the live slide";

        FollowScreenButton.IsEnabled = hasGroup;
        Highlight(FollowScreenButton, source == ColourSources.Screen);
        FollowScreenButton.ToolTip = _captureStatus
            ?? (!hasGroup
                ? "Add a group in Settings → Lights & groups"
                : source == ColourSources.Screen
                    ? $"{group} is following {DisplayName()} live — animated backgrounds included"
                    : $"Click to match {group} to {DisplayName()}, continuously");

        GroupOffButton.IsEnabled = hasGroup;
        Highlight(GroupOffButton, source == ColourSources.Off);
        GroupOffButton.ToolTip = !hasGroup
            ? "Add a group in Settings → Lights & groups"
            : source == ColourSources.Off
                ? $"{group} is held dark"
                : $"Click to hold {group} dark — other groups keep running";

        // Breathing layers on top of the source, so this stays available whatever the three
        // buttons above are showing. The audio status is only worth surfacing while a group is
        // actually breathing — that's the only time the input is open and a failure matters.
        bool breathing = hasGroup && _engine.PulseOf(groupId!) == PulseModes.Breathe;
        BreatheButton.IsEnabled = hasGroup;
        Highlight(BreatheButton, breathing);
        BreatheButton.ToolTip = !hasGroup
            ? "Add a group in Settings → Lights & groups"
            : breathing
                ? _audioStatus ?? $"{group} is breathing with the music — click to hold it steady"
                : $"Click to ride {group}'s brightness on the music — its colour still comes from the source on the left";

        // The swatches set a manual colour, which is a source in its own right.
        foreach (var button in FavouritePanel.Children.OfType<WpfButton>())
        {
            button.IsEnabled = hasGroup;
            button.BorderBrush = source == ColourSources.Manual ? ModeActiveBrush : ModeFaintBorderBrush;
        }
    }

    private string DisplayName() =>
        string.IsNullOrWhiteSpace(_settings.CaptureDisplay)
            ? "the main display"
            : MonitorInfo.Describe(_settings.CaptureDisplay, null);

    private static void Highlight(WpfButton button, bool active)
    {
        button.Background = active ? ModeActiveBrush : Brushes.Transparent;
        button.Foreground = active ? Brushes.White : ModeMutedBrush;
        button.BorderBrush = active ? ModeActiveBrush : ModeFaintBorderBrush;
    }

    private void PositionPickerNearPill(Window picker)
    {
        // Drop it just below the pill, nudged so it stays on the working area.
        picker.WindowStartupLocation = WindowStartupLocation.Manual;
        picker.SizeToContent = SizeToContent.WidthAndHeight;
        picker.Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            double left = Left;
            double top = Top + ActualHeight + 8;
            if (left + picker.ActualWidth > area.Right) left = area.Right - picker.ActualWidth - 8;
            if (top + picker.ActualHeight > area.Bottom) top = Top - picker.ActualHeight - 8;
            picker.Left = Math.Max(area.Left + 8, left);
            picker.Top = Math.Max(area.Top + 8, top);
        };
    }

    private void FavouriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton button && button.Tag is int index)
            ApplyFavouriteColour(index);
    }

    private void FavouriteButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is WpfButton button && button.Tag is int index)
        {
            e.Handled = true;
            EditFavouriteColour(index);
        }
    }

    private void EditFavouriteColour(int index)
    {
        if (index < 0 || index >= _settings.FavouriteColours.Length)
            return;

        var picker = new ColourPickerWindow(ParseColour(_settings.FavouriteColours[index])) { Owner = this };
        PositionPickerNearPill(picker);

        if (picker.ShowDialog() == true)
            SaveFavouriteColour(index, picker.SelectedColour);
    }

    private void ApplyFavouriteColour(int index)
    {
        if (index < 0 || index >= _settings.FavouriteColours.Length)
            return;

        var colour = ParseColour(_settings.FavouriteColours[index]);
        ApplyColour(colour);
    }

    private void SaveFavouriteColour(int index, WpfColor colour)
    {
        if (index < 0 || index >= _settings.FavouriteColours.Length)
            return;

        _settings.FavouriteColours[index] = ToHex(colour);
        _settings.Save();
        RefreshFavouriteButtons();
    }

    private void ApplyColour(WpfColor colour)
    {
        // Hand the colour to the engine and let its OutputChanged callback drive the
        // swatch as it fades. Setting the swatch here as well makes it flash the target
        // colour and then jump back to the fading value on the next frame.
        if (ActiveGroupId is { } id)
            _engine.SetGroupManualColour(id, colour);
    }

    private void RefreshFavouriteButtons()
    {
        for (int i = 0; i < FavouritePanel.Children.Count; i++)
        {
            if (FavouritePanel.Children[i] is WpfButton button)
            {
                var colour = ParseColour(_settings.FavouriteColours[Math.Clamp(i, 0, _settings.FavouriteColours.Length - 1)]);
                button.Background = new SolidColorBrush(colour);
            }
        }
    }

    private static WpfColor ParseColour(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Colors.Black;

        try
        {
            return (WpfColor)WpfColorConverter.ConvertFromString(value);
        }
        catch
        {
            return Colors.Black;
        }
    }

    private static string ToHex(WpfColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings, _engine, _client, _screen, _audio, _update, _remote) { Owner = this };
        dialog.ShowDialog();
        RebuildGroupPicker();
        RefreshScenePicker();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        Close();
    }
}
