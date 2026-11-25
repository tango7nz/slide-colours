using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Win32;
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
    private readonly ScreenColourSource _screen;
    private readonly AudioLevelSource _audio;
    private readonly UpdateService _update;
    private readonly RemoteControlServer _remote;
    private bool _loaded;

    public SettingsWindow(AppSettings settings, LightingEngine engine, ProPresenterClient client,
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
        StartAudioMeter();

        VersionText.Text = $"Current version: {UpdateService.CurrentVersion.ToString(3)}";
        ShowUpdateState(_update.Available);
        ShowSettingsFilePath();
        LoadFromSettings();
    }

    /// <summary>
    /// Pushes every value from <see cref="_settings"/> into the controls. Called on open and again
    /// after a settings file is loaded, so the dialog can't drift out of step with what's stored.
    /// </summary>
    private void LoadFromSettings()
    {
        _loaded = false;

        AutoCheckUpdatesCheck.IsChecked = _settings.AutoCheckForUpdates;

        HostBox.Text = _settings.ProPresenterHost;
        PortBox.Text = _settings.ProPresenterPort.ToString();

        SelectByTag(ProtocolBox, _settings.Protocol);
        TargetIpBox.Text = _settings.TargetIp;
        UniverseBox.Text = _settings.Universe.ToString();
        RefreshRigSummary();

        ComPortBox.Items.Clear();
        foreach (var name in SerialPort.GetPortNames().Distinct().OrderBy(n => n))
            ComPortBox.Items.Add(name);
        ComPortBox.Text = _settings.ComPort;

        PopulateDisplays();
        SmoothingBox.Text = _settings.CaptureSmoothingMs.ToString();
        SelectByTag(CaptureFpsBox, _settings.CaptureFps.ToString(), "30");

        PopulateAudioDevices();
        AudioGateSlider.Value = Math.Clamp(_settings.AudioGateDb, -80, -20);
        SelectByTag(AudioResponseBox, _settings.AudioResponseMs.ToString(), "400");

        FadeBox.Text = _settings.FadeMs.ToString();
        BrightnessSlider.Value = _settings.BrightnessPercent;
        SaturationSlider.Value = _settings.SaturationBoostPercent;
        FullBrightnessCheck.IsChecked = _settings.FullBrightnessColours;
        SelectByTag(FallbackBox, _settings.Fallback);
        StartEnabledCheck.IsChecked = _settings.StartEnabled;
        PopulateStartupGroups();
        PopulateStartupScenes();

        RemoteEnabledCheck.IsChecked = _settings.RemoteEnabled;
        RemotePortBox.Text = _settings.RemotePort.ToString();
        RemoteKeyBox.Text = _settings.RemoteKey;

        _loaded = true;
        UpdateSliderLabels();
        UpdateProtocolFields();
        UpdateRemoteFields();

        _engine.TestFixtureChanged += OnTestFixtureChanged;
        Closed += (_, _) =>
        {
            // Without the name readout a running chase is just unexplained flashing, so stop it.
            _engine.TestFixtureChanged -= OnTestFixtureChanged;
            _engine.StopPatchTest();
        };
    }

    private static void SelectByTag(WpfComboBox box, string tag, string? fallbackTag = null)
    {
        foreach (WpfComboBoxItem item in box.Items)
        {
            if ((string)item.Tag == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (fallbackTag != null)
            SelectByTag(box, fallbackTag);
        else
            box.SelectedIndex = 0;
    }

    /// <summary>
    /// Lists the displays DXGI can capture. A blank tag means "whichever is primary", so the
    /// setting still works if monitors are re-arranged.
    /// </summary>
    private void PopulateDisplays()
    {
        CaptureDisplayBox.Items.Clear();
        CaptureDisplayBox.Items.Add(new WpfComboBoxItem { Tag = "", Content = "Main display (primary screen)" });

        foreach (var display in DesktopDuplicator.List())
            CaptureDisplayBox.Items.Add(new WpfComboBoxItem { Tag = display.DeviceName, Content = display.Label });

        // A display saved earlier may be unplugged right now — keep it selectable rather than
        // silently switching them back to the primary screen.
        string saved = _settings.CaptureDisplay;
        if (!string.IsNullOrWhiteSpace(saved)
            && !CaptureDisplayBox.Items.Cast<WpfComboBoxItem>().Any(i => (string)i.Tag == saved))
        {
            CaptureDisplayBox.Items.Add(new WpfComboBoxItem
            {
                Tag = saved,
                Content = MonitorInfo.Describe(saved, "not connected"),
            });
        }

        SelectByTag(CaptureDisplayBox, saved);
    }

    private static string SelectedTag(WpfComboBox box) =>
        (box.SelectedItem as WpfComboBoxItem)?.Tag as string ?? "";

    /// <summary>
    /// Lists the groups that can be pre-selected on launch. A blank tag keeps the old behaviour of
    /// reopening on whichever group was in use when the app was last closed.
    /// </summary>
    /// <param name="keepSelection">
    /// True after editing the rig, where the user's unsaved choice in this box should survive.
    /// False when the settings themselves have just changed underneath us.
    /// </param>
    private void PopulateStartupGroups(bool keepSelection = false)
    {
        string wanted = keepSelection && SelectedTag(StartupGroupBox) is { Length: > 0 } chosen
            ? chosen
            : _settings.StartupGroupId;

        StartupGroupBox.Items.Clear();
        StartupGroupBox.Items.Add(new WpfComboBoxItem { Tag = "", Content = "Last used" });
        foreach (var group in _settings.Groups)
            StartupGroupBox.Items.Add(new WpfComboBoxItem { Tag = group.Id, Content = group.DisplayName });

        SelectByTag(StartupGroupBox, wanted);
    }

    private void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded)
            UpdateProtocolFields();
    }

    private void UpdateProtocolFields()
    {
        string protocol = SelectedTag(ProtocolBox);
        bool serial = protocol == "enttec";
        bool xpress = protocol == "xpress";
        var network = !serial && !xpress ? Visibility.Visible : Visibility.Collapsed;
        var com = serial ? Visibility.Visible : Visibility.Collapsed;

        TargetIpLabel.Visibility = network;
        TargetIpPanel.Visibility = network;
        UniverseLabel.Visibility = network;
        UniverseBox.Visibility = network;
        ComPortLabel.Visibility = com;
        ComPortBox.Visibility = com;
        XpressNote.Visibility = xpress ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RemoteEnabled_Changed(object sender, RoutedEventArgs e)
    {
        // Turning it on for the first time mints a key, so the listener is never silently open to
        // the whole network. Clearing the box afterwards is an explicit opt-out.
        if (_loaded && RemoteEnabledCheck.IsChecked == true && RemoteKeyBox.Text.Trim().Length == 0)
            RemoteKeyBox.Text = NewKey();

        if (_loaded)
            UpdateRemoteFields();
    }

    private void RemoteKey_Click(object sender, RoutedEventArgs e)
    {
        RemoteKeyBox.Text = NewKey();
        UpdateRemoteFields();
    }

    private static string NewKey() => Guid.NewGuid().ToString("N")[..12];

    private void UpdateRemoteFields()
    {
        bool on = RemoteEnabledCheck.IsChecked == true;
        RemotePortLabel.IsEnabled = on;
        RemotePortBox.IsEnabled = on;
        RemoteKeyLabel.IsEnabled = on;
        RemoteKeyPanel.IsEnabled = on;

        RemoteHintText.Inlines.Clear();

        if (!on)
        {
            RemoteHintText.Inlines.Add("Off — nothing on the network can control this app.");
            return;
        }

        string key = RemoteKeyBox.Text.Trim();
        string suffix = key.Length > 0 ? $"?key={key}" : "";
        string port = RemotePortBox.Text.Trim() is { Length: > 0 } p ? p : "7373";
        string url = $"http://{RemoteControlServer.LocalAddress()}:{port}";

        // The key has to be on the index URL too, or the browser just gets a 401.
        string indexUrl = $"{url}/{suffix}";

        RemoteHintText.Inlines.Add("Open ");
        // A half-typed port leaves the text unparseable, so fall back to plain text rather than throw.
        if (Uri.TryCreate(indexUrl, UriKind.Absolute, out var uri))
        {
            var link = new Hyperlink(new Run(indexUrl)) { NavigateUri = uri };
            link.RequestNavigate += Hyperlink_RequestNavigate;
            RemoteHintText.Inlines.Add(link);
        }
        else
        {
            RemoteHintText.Inlines.Add(indexUrl);
        }

        RemoteHintText.Inlines.Add(" in a browser for the list of URLs to paste into Companion.\n");
        RemoteHintText.Inlines.Add($"Recall a scene with {url}/scene/YourSceneName{suffix}");
        if (key.Length == 0)
            RemoteHintText.Inlines.Add("\n\nNo access key: anyone on this network can control the lights.");
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
        AudioGateValue.Text = $"{(int)AudioGateSlider.Value} dB";
    }

    /// <summary>
    /// Lists the audio inputs, with the default playback device first as the one entry guaranteed
    /// to exist. A device saved earlier may be unplugged right now — a USB interface that isn't
    /// switched on yet — so it stays selectable rather than silently reverting.
    /// </summary>
    private void PopulateAudioDevices()
    {
        AudioDeviceBox.Items.Clear();
        AudioDeviceBox.Items.Add(new WpfComboBoxItem { Tag = "", Content = "Default playback device (what this PC plays)" });

        foreach (var device in AudioLevelSource.List())
            AudioDeviceBox.Items.Add(new WpfComboBoxItem { Tag = device.Id, Content = device.Label });

        string saved = _settings.AudioDeviceId;
        if (!string.IsNullOrWhiteSpace(saved)
            && !AudioDeviceBox.Items.Cast<WpfComboBoxItem>().Any(i => (string)i.Tag == saved))
        {
            AudioDeviceBox.Items.Add(new WpfComboBoxItem { Tag = saved, Content = "Saved device (not connected)" });
        }

        SelectByTag(AudioDeviceBox, saved);
    }

    /// <summary>
    /// Switching device takes effect straight away rather than on Save, because the whole point of
    /// the meter below is to try one and watch what it does.
    /// </summary>
    private void AudioDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded)
            return;
        _settings.AudioDeviceId = SelectedTag(AudioDeviceBox);
        _audio.Restart();
    }

    /// <summary>
    /// The gate is live for the same reason: you set it by talking near the mic and pushing it up
    /// until the meter stops moving, which needs the change applied as the slider moves.
    /// </summary>
    private void AudioGate_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded)
            return;
        _settings.AudioGateDb = (int)AudioGateSlider.Value;
        UpdateSliderLabels();
    }

    /// <summary>
    /// Runs the meter while this window is open, and forces audio capture on for the duration —
    /// otherwise the gate could only be set after a group was already breathing, which is exactly
    /// the wrong way round.
    /// </summary>
    private void StartAudioMeter()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        timer.Tick += (_, _) =>
        {
            AudioMeter.Value = _audio.Level * 100;
            ShowAudioStatus(_audio.Status);
        };

        // The device and gate are applied as they're changed, which means they've already taken
        // effect if the dialog is then cancelled. Remember them so Cancel still means cancel.
        string device = _settings.AudioDeviceId;
        int gate = _settings.AudioGateDb;

        _audio.SetActive(true);
        timer.Start();

        Closed += (_, _) =>
        {
            timer.Stop();
            if (DialogResult != true)
            {
                _settings.AudioDeviceId = device;
                _settings.AudioGateDb = gate;
                _audio.Restart();
            }
            // Back to only running when a group actually needs it.
            _audio.SetActive(_engine.AudioNeeded);
        };
    }

    private void ShowAudioStatus(string? status)
    {
        if (status == null)
        {
            AudioStatusText.Text = "Listening. Talk near the mic — if the meter moves, raise the gate.";
            AudioStatusText.Foreground = System.Windows.Media.Brushes.Gray;
            return;
        }

        AudioStatusText.Text = status;
        AudioStatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
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

        if (!int.TryParse(FadeBox.Text.Trim(), out int fadeMs) || fadeMs < 0)
            fadeMs = _settings.FadeMs;

        if (!int.TryParse(SmoothingBox.Text.Trim(), out int smoothingMs) || smoothingMs < 0)
            smoothingMs = _settings.CaptureSmoothingMs;
        if (!int.TryParse(SelectedTag(CaptureFpsBox), out int captureFps))
            captureFps = _settings.CaptureFps;

        if (!int.TryParse(RemotePortBox.Text.Trim(), out int remotePort) || remotePort < 1 || remotePort > 65535)
        {
            WpfMessageBox.Show(this, "The remote control port must be between 1 and 65535.", "Slide Colours");
            return false;
        }

        bool endpointChanged = _settings.ProPresenterHost != HostBox.Text.Trim()
                            || _settings.ProPresenterPort != port;
        bool remoteChanged = _settings.RemoteEnabled != (RemoteEnabledCheck.IsChecked == true)
                          || _settings.RemotePort != remotePort
                          || _settings.RemoteKey != RemoteKeyBox.Text.Trim();
        bool displayChanged = _settings.CaptureDisplay != SelectedTag(CaptureDisplayBox);

        if (!int.TryParse(SelectedTag(AudioResponseBox), out int audioResponseMs))
            audioResponseMs = _settings.AudioResponseMs;

        _settings.ProPresenterHost = HostBox.Text.Trim();
        _settings.ProPresenterPort = port;
        _settings.Protocol = protocol;
        _settings.TargetIp = TargetIpBox.Text.Trim();
        _settings.Universe = universe;
        _settings.ComPort = ComPortBox.Text.Trim();
        _settings.CaptureDisplay = SelectedTag(CaptureDisplayBox);
        _settings.CaptureSmoothingMs = smoothingMs;
        _settings.CaptureFps = captureFps;
        // Device and gate are already live — see AudioDevice_Changed — so only the settle time is
        // new here, and it's read fresh on every buffer.
        _settings.AudioDeviceId = SelectedTag(AudioDeviceBox);
        _settings.AudioGateDb = (int)AudioGateSlider.Value;
        _settings.AudioResponseMs = audioResponseMs;
        _settings.FadeMs = fadeMs;
        _settings.BrightnessPercent = (int)BrightnessSlider.Value;
        _settings.SaturationBoostPercent = (int)SaturationSlider.Value;
        _settings.FullBrightnessColours = FullBrightnessCheck.IsChecked == true;
        _settings.Fallback = SelectedTag(FallbackBox);
        _settings.RemoteEnabled = RemoteEnabledCheck.IsChecked == true;
        _settings.RemotePort = remotePort;
        _settings.RemoteKey = RemoteKeyBox.Text.Trim();
        _settings.StartEnabled = StartEnabledCheck.IsChecked == true;
        _settings.StartupGroupId = SelectedTag(StartupGroupBox);
        _settings.StartupSceneId = SelectedTag(StartupSceneBox);
        _settings.AutoCheckForUpdates = AutoCheckUpdatesCheck.IsChecked == true;

        _settings.Save();
        _engine.ReloadRig();
        if (endpointChanged)
            _client.Reconnect();
        if (displayChanged)
            _screen.Restart();
        if (remoteChanged)
        {
            _remote.Restart();
            if (_remote.Status is { } problem)
                WpfMessageBox.Show(this, problem, "Slide Colours", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

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
        if (_engine.TestRunning)
        {
            _engine.StopPatchTest();
            return;
        }

        if (!Apply())
            return;

        if (_settings.Fixtures.Count == 0)
        {
            WpfMessageBox.Show(this, "Add a light in Lights & groups first.", "Slide Colours");
            return;
        }

        _engine.RunPatchTest(_settings.Fixtures);
    }

    private void OnTestFixtureChanged(Fixture? fixture) =>
        Dispatcher.BeginInvoke(() => ShowTestFixture(fixture));

    /// <summary>
    /// Names the light currently lit by the patch test, so you can match what's on stage to what's
    /// in the rig. Null means the test has finished or been stopped.
    /// </summary>
    private void ShowTestFixture(Fixture? fixture)
    {
        if (fixture == null)
        {
            TestButton.Content = "Test lights";
            TestStatusText.Text = "";
            return;
        }

        int position = _settings.Fixtures.FindIndex(f => f.Id == fixture.Id) + 1;
        string where = position > 0 && _settings.Fixtures.Count > 1
            ? $"  ({position} of {_settings.Fixtures.Count})"
            : "";
        TestButton.Content = "Stop test";
        TestStatusText.Text = $"Lighting: {fixture.Name}{where}";
    }

    /// <param name="keepSelection">See <see cref="PopulateStartupGroups"/>.</param>
    private void PopulateStartupScenes(bool keepSelection = false)
    {
        string wanted = keepSelection && SelectedTag(StartupSceneBox) is { Length: > 0 } chosen
            ? chosen
            : _settings.StartupSceneId;

        StartupSceneBox.Items.Clear();
        StartupSceneBox.Items.Add(new WpfComboBoxItem { Tag = "", Content = "Don't recall a scene" });
        foreach (var scene in _settings.Scenes)
            StartupSceneBox.Items.Add(new WpfComboBoxItem { Tag = scene.Id, Content = scene.DisplayName });

        SelectByTag(StartupSceneBox, wanted);
    }

    private void Scenes_Click(object sender, RoutedEventArgs e)
    {
        // The scene editor saves straight through and captures the live rig, so apply this dialog's
        // pending edits first — otherwise its Save would persist stale values from around it.
        if (!Apply())
            return;

        var dialog = new SceneWindow(_settings, _engine) { Owner = this };
        if (dialog.ShowDialog() == true)
            PopulateStartupScenes(keepSelection: true);
    }

    private void ShowSettingsFilePath() =>
        SettingsPathText.Text = $"Currently using {AppSettings.CurrentFilePath}";

    private void SaveToFile_Click(object sender, RoutedEventArgs e)
    {
        // Write what's on screen, not what was last saved.
        if (!Apply())
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Save Slide Colours settings",
            FileName = $"SlideColours-settings-{DateTime.Now:yyyy-MM-dd}.json",
            DefaultExt = ".json",
            Filter = "Slide Colours settings (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            _settings.SaveTo(dialog.FileName);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, $"Couldn't save that file:\n\n{ex.Message}", "Slide Colours",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadFromFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load Slide Colours settings",
            DefaultExt = ".json",
            Filter = "Slide Colours settings (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        AppSettings loaded;
        try
        {
            loaded = AppSettings.LoadFrom(dialog.FileName);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, $"Couldn't read that file:\n\n{ex.Message}", "Slide Colours",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = WpfMessageBox.Show(this,
            $"Replace all current settings with {Path.GetFileName(dialog.FileName)}?\n\n" +
            $"This file has {loaded.Fixtures.Count} light(s) in {loaded.Groups.Count} group(s). " +
            "Your current lights, groups and settings will be overwritten.",
            "Slide Colours", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
            return;

        // Where the pill sits belongs to this screen, not to the config — importing a position from
        // another machine could drop it off-screen.
        double left = _settings.WindowLeft, top = _settings.WindowTop;
        _settings.CopyFrom(loaded);
        _settings.WindowLeft = left;
        _settings.WindowTop = top;

        _settings.Save();
        _engine.ReloadRig();
        _client.Reconnect();
        _screen.Restart();
        _audio.Restart();

        // The imported file carries its own remote settings, so the listener has to be brought into
        // line here. Apply() can't do it afterwards: the fields below now match what was imported,
        // so it sees nothing changed and leaves the server as it was.
        _remote.Restart();
        if (_remote.Status is { } remoteProblem)
            WpfMessageBox.Show(this, remoteProblem, "Slide Colours", MessageBoxButton.OK, MessageBoxImage.Warning);

        LoadFromSettings();
    }

    private void Rig_Click(object sender, RoutedEventArgs e)
    {
        // The rig editor saves straight through, so the rest of this dialog's fields are applied
        // first — otherwise its Save would persist settings that still hold the old values.
        if (!Apply())
            return;

        var dialog = new RigWindow(_settings, _engine) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            RefreshRigSummary();
            // Groups may have been renamed, added or deleted underneath this dialog, and deleting
            // one prunes it out of every scene.
            PopulateStartupGroups(keepSelection: true);
            PopulateStartupScenes(keepSelection: true);
        }
    }

    private void RefreshRigSummary()
    {
        int lights = _settings.Fixtures.Count;
        int groups = _settings.Groups.Count;
        int scenes = _settings.Scenes.Count;
        RigSummaryText.Text = $"{lights} {(lights == 1 ? "light" : "lights")}, "
                            + $"{groups} {(groups == 1 ? "group" : "groups")}, "
                            + $"{scenes} {(scenes == 1 ? "scene" : "scenes")}";
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
