using System.Windows;
using SlideColours.Models;
using SlideColours.Services;

namespace SlideColours;

public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource _cts = new();
    private AppSettings _settings = null!;
    private LightingEngine _engine = null!;
    private ProPresenterClient _client = null!;
    private ScreenColourSource _screen = null!;
    private UpdateService _update = null!;
    private RemoteControlServer _remote = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        for (int i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i] == "--settings")
                AppSettings.OverridePath = e.Args[i + 1];

        _settings = AppSettings.Load();

        _engine = new LightingEngine(_settings) { Enabled = _settings.StartEnabled };
        _engine.Start();

        _client = new ProPresenterClient(_settings);
        _client.SlideImageReceived += _engine.OnSlideImage;
        _client.SlideCleared += _engine.OnSlideCleared;
        _ = _client.RunAsync(_cts.Token);

        _screen = new ScreenColourSource(_settings);
        _screen.ColourSampled += _engine.OnScreenColour;

        // Capture only runs while at least one group is following the screen. Group sources live in
        // the settings file, so the app comes back up driving each group the way it was left.
        _engine.ScreenNeededChanged += needed => _screen.SetActive(needed);
        _screen.SetActive(_engine.ScreenNeeded);

        _update = new UpdateService();

        // Off unless the user has turned it on; Restart() is a no-op when disabled.
        _remote = new RemoteControlServer(_settings, _engine);
        _remote.Restart();

        MainWindow = new MainWindow(_settings, _engine, _client, _screen, _update, _remote);
        MainWindow.Show();

        // Check for a new version in the background (unless the user turned this off); the window
        // shows a dot on the settings cog if one is found. Never blocks startup, never surfaces
        // errors here. The Settings "Check for updates" button still works regardless.
        if (_settings.AutoCheckForUpdates)
            _ = _update.CheckAsync(_cts.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts.Cancel();
        _remote.Dispose();
        _screen.Dispose();
        _engine.Dispose();
        _settings.Save();
        base.OnExit(e);
    }
}
