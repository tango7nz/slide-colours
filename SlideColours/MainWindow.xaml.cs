using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SlideColours.Models;
using SlideColours.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfButton = System.Windows.Controls.Button;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace SlideColours;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly LightingEngine _engine;
    private readonly ProPresenterClient _client;
    private readonly UpdateService _update;

    private bool _connected;
    private bool _following = true;

    private static readonly WpfBrush ModeActiveBrush = new SolidColorBrush(WpfColor.FromRgb(0x3A, 0x6F, 0xF0));
    private static readonly WpfBrush ModeMutedBrush = new SolidColorBrush(WpfColor.FromRgb(0xB8, 0xB8, 0xC0));
    private static readonly WpfBrush ModeFaintBorderBrush = new SolidColorBrush(WpfColor.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    public MainWindow(AppSettings settings, LightingEngine engine, ProPresenterClient client, UpdateService update)
    {
        _settings = settings;
        _engine = engine;
        _client = client;
        _update = update;

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
        _following = _engine.FollowSlides;
        _connected = _client.Connected;
        RefreshFollowButton();

        _engine.ModeChanged += following => Dispatcher.BeginInvoke(() =>
        {
            _following = following;
            RefreshFollowButton();
        });

        _client.ConnectionChanged += connected => Dispatcher.BeginInvoke(() =>
        {
            _connected = connected;
            RefreshFollowButton();
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

        // Size isn't known until the pill has laid out; keep it fully on-screen.
        Loaded += (_, _) => ClampToScreen();
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

    private void FollowSlide_Click(object sender, RoutedEventArgs e) => _engine.SetFollowSlides();

    private void RefreshFollowButton()
    {
        // Following slides is only possible while ProPresenter is connected.
        FollowSlideButton.IsEnabled = _connected;
        FollowSlideButton.Background = _following ? ModeActiveBrush : Brushes.Transparent;
        FollowSlideButton.Foreground = _following ? Brushes.White : ModeMutedBrush;
        FollowSlideButton.BorderBrush = _following ? ModeActiveBrush : ModeFaintBorderBrush;
        FollowSlideButton.ToolTip = !_connected
            ? $"ProPresenter not connected ({_settings.ProPresenterHost}:{_settings.ProPresenterPort}) — slide following unavailable"
            : _following
                ? "Following the live slide"
                : "Click to match the stage colour to the live slide";
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
        _engine.SetManualColour(colour);
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
        var dialog = new SettingsWindow(_settings, _engine, _client, _update) { Owner = this };
        dialog.ShowDialog();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        Close();
    }
}
