using System.Diagnostics;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using SlideColours.Models;
using SlideColours.Services;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfMessageBox = System.Windows.MessageBox;

namespace SlideColours;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly LightingEngine _engine;
    private readonly ProPresenterClient _client;
    private readonly UpdateService _update;
    private bool _loaded;

    public SettingsWindow(AppSettings settings, LightingEngine engine, ProPresenterClient client, UpdateService update)
    {
        _settings = settings;
        _engine = engine;
        _client = client;
        _update = update;

        InitializeComponent();

        VersionText.Text = $"Current version: {UpdateService.CurrentVersion.ToString(3)}";
        AutoCheckUpdatesCheck.IsChecked = settings.AutoCheckForUpdates;
        ShowUpdateState(_update.Available);

        HostBox.Text = settings.ProPresenterHost;
        PortBox.Text = settings.ProPresenterPort.ToString();

        SelectByTag(ProtocolBox, settings.Protocol);
        TargetIpBox.Text = settings.TargetIp;
        UniverseBox.Text = settings.Universe.ToString();
        StartChannelBox.Text = settings.StartChannel.ToString();
        DimmerCheck.IsChecked = settings.HasMasterDimmer;

        foreach (var name in SerialPort.GetPortNames().Distinct().OrderBy(n => n))
            ComPortBox.Items.Add(name);
        ComPortBox.Text = settings.ComPort;

        FadeBox.Text = settings.FadeMs.ToString();
        BrightnessSlider.Value = settings.BrightnessPercent;
        SaturationSlider.Value = settings.SaturationBoostPercent;
        FullBrightnessCheck.IsChecked = settings.FullBrightnessColours;
        SelectByTag(FallbackBox, settings.Fallback);
        StartEnabledCheck.IsChecked = settings.StartEnabled;

        _loaded = true;
        UpdateSliderLabels();
        UpdateProtocolFields();
    }

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
        (box.SelectedItem as WpfComboBoxItem)?.Tag as string ?? "";

    private void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded)
            UpdateProtocolFields();
    }

    private void UpdateProtocolFields()
    {
        bool serial = SelectedTag(ProtocolBox) == "enttec";
        var network = serial ? Visibility.Collapsed : Visibility.Visible;
        var com = serial ? Visibility.Visible : Visibility.Collapsed;

        TargetIpLabel.Visibility = network;
        TargetIpPanel.Visibility = network;
        UniverseLabel.Visibility = network;
        UniverseBox.Visibility = network;
        ComPortLabel.Visibility = com;
        ComPortBox.Visibility = com;
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loaded)
            UpdateSliderLabels();
    }

    private void UpdateSliderLabels()
    {
        BrightnessValue.Text = $"{(int)BrightnessSlider.Value}%";
        SaturationValue.Text = $"{(int)SaturationSlider.Value}%";
    }

    private bool Apply()
    {
        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port < 1 || port > 65535)
        {
            WpfMessageBox.Show(this, "Port must be a number between 1 and 65535.", "Slide Colours");
            return false;
        }

        string protocol = SelectedTag(ProtocolBox);

        if (!int.TryParse(UniverseBox.Text.Trim(), out int universe))
            universe = _settings.Universe;
        if (protocol == "sacn" && universe < 1)
        {
            WpfMessageBox.Show(this, "sACN universes start at 1.", "Slide Colours");
            return false;
        }

        if (!int.TryParse(StartChannelBox.Text.Trim(), out int startChannel) || startChannel < 1 || startChannel > 512)
        {
            WpfMessageBox.Show(this, "Start channel must be between 1 and 512.", "Slide Colours");
            return false;
        }

        if (!int.TryParse(FadeBox.Text.Trim(), out int fadeMs) || fadeMs < 0)
            fadeMs = _settings.FadeMs;

        bool endpointChanged = _settings.ProPresenterHost != HostBox.Text.Trim()
                            || _settings.ProPresenterPort != port;

        _settings.ProPresenterHost = HostBox.Text.Trim();
        _settings.ProPresenterPort = port;
        _settings.Protocol = protocol;
        _settings.TargetIp = TargetIpBox.Text.Trim();
        _settings.Universe = universe;
        _settings.StartChannel = startChannel;
        _settings.HasMasterDimmer = DimmerCheck.IsChecked == true;
        _settings.ComPort = ComPortBox.Text.Trim();
        _settings.FadeMs = fadeMs;
        _settings.BrightnessPercent = (int)BrightnessSlider.Value;
        _settings.SaturationBoostPercent = (int)SaturationSlider.Value;
        _settings.FullBrightnessColours = FullBrightnessCheck.IsChecked == true;
        _settings.Fallback = SelectedTag(FallbackBox);
        _settings.StartEnabled = StartEnabledCheck.IsChecked == true;
        _settings.AutoCheckForUpdates = AutoCheckUpdatesCheck.IsChecked == true;

        _settings.Save();
        _engine.RefreshOutput();
        if (endpointChanged)
            _client.Reconnect();

        return true;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        object original = ScanButton.Content;
        ScanButton.Content = "Scanning…";
        try
        {
            var nodes = await ArtNetDiscovery.DiscoverAsync();
            if (nodes.Count == 0)
            {
                WpfMessageBox.Show(this,
                    "No DMX nodes responded to Art-Net discovery.\n\n" +
                    "Check the node is powered on and on the same network, " +
                    "or leave Target IP blank to broadcast.",
                    "Slide Colours");
                return;
            }

            var dialog = new DiscoveryWindow(nodes) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.SelectedIp != null)
                TargetIpBox.Text = dialog.SelectedIp;
        }
        finally
        {
            ScanButton.Content = original;
            ScanButton.IsEnabled = true;
        }
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (Apply())
            _engine.RunTestSweep();
    }

    private void ShowUpdateState(UpdateInfo? info)
    {
        if (info != null)
        {
            UpdateStatusText.Text = $"Version {info.Version.ToString(3)} is available.";
            UpdateStatusText.Visibility = Visibility.Visible;
            InstallUpdateButton.Visibility = Visibility.Visible;

            bool hasNotes = Uri.TryCreate(info.ReleaseUrl, UriKind.Absolute, out var uri);
            if (hasNotes)
                ReleaseNotesLink.NavigateUri = uri;
            ReleaseNotesText.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            InstallUpdateButton.Visibility = Visibility.Collapsed;
            ReleaseNotesText.Visibility = Visibility.Collapsed;
        }
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            // Works for http(s) links (browser) and mailto: links (mail client).
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // No default browser / mail client, or launch blocked — nothing useful to do.
        }
        e.Handled = true;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking…";
        UpdateStatusText.Visibility = Visibility.Visible;
        try
        {
            var info = await _update.CheckAsync();
            if (info != null)
                ShowUpdateState(info);
            else
                UpdateStatusText.Text = "You're on the latest version.";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        var info = _update.Available;
        if (info == null)
            return;

        var confirm = WpfMessageBox.Show(this,
            $"Download and install version {info.Version.ToString(3)}?\n\n" +
            "Slide Colours will close and reopen. A copy of the current version is kept next to the app.",
            "Slide Colours", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
            return;

        InstallUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        var progress = new Progress<double>(p => UpdateStatusText.Text = $"Downloading… {p:P0}");
        try
        {
            await _update.DownloadAndApplyAsync(info, progress);
            // The new exe has launched; close this instance so it can take over.
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update failed: {ex.Message}";
            InstallUpdateButton.IsEnabled = true;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Apply())
            DialogResult = true;
    }
}
