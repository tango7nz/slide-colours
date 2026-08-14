using System.Windows;
using SlideColours.Models;
using SlideColours.Services;

namespace SlideColours;

public partial class App : System.Windows.Application
{
    /// <summary>
    /// Held for the life of the process to keep a second copy from starting. Two instances would
    /// both transmit to the same universe 30 times a second and fight over the frame, both write
    /// settings.json, and the second one's remote-control listener would fail to bind.
    ///
    /// Local rather than Global: one instance per Windows session is the right rule, and a Global
    /// name would also need an ACL to work across accounts on a shared booth PC.
    /// </summary>
    private static Mutex? _instanceLock;

    private readonly CancellationTokenSource _cts = new();
    private AppSettings _settings = null!;
    private LightingEngine _engine = null!;
    private ProPresenterClient _client = null!;
    private ScreenColourSource _screen = null!;
    private AudioLevelSource _audio = null!;
    private UpdateService _update = null!;
    private RemoteControlServer _remote = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // First, so that everything below — including a rejected second copy's own shutdown — is
        // covered by it.
        InstallLastChanceHandlers();

        if (!ClaimSingleInstance())
            return;

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

        // Same arrangement for audio: the device is only opened while some group is breathing, and
        // an input that breaks holds those groups at full rather than leaving them dim on stage.
        _audio = new AudioLevelSource(_settings);
        _audio.LevelSampled += _engine.OnAudioLevel;
        _audio.StatusChanged += status => _engine.SetAudioHealthy(status == null);
        _engine.AudioNeededChanged += needed => _audio.SetActive(needed);
        _audio.SetActive(_engine.AudioNeeded);

        _update = new UpdateService();

        // Off unless the user has turned it on; Restart() is a no-op when disabled.
        _remote = new RemoteControlServer(_settings, _engine);
        _remote.Restart();

        MainWindow = new MainWindow(_settings, _engine, _client, _screen, _audio, _update, _remote);
        MainWindow.Show();

        // Check for a new version in the background (unless the user turned this off); the window
        // shows a dot on the settings cog if one is found. Never blocks startup, never surfaces
        // errors here. The Settings "Check for updates" button still works regardless.
        if (_settings.AutoCheckForUpdates)
            _ = _update.CheckAsync(_cts.Token);
    }

    /// <summary>
    /// Takes the single-instance lock, or tells the user which app is already running and asks WPF
    /// to shut this copy down. Returns false when startup must not continue.
    /// </summary>
    private bool ClaimSingleInstance()
    {
        try
        {
            _instanceLock = new Mutex(initiallyOwned: true, @"Local\SlideColours", out bool mine);
            if (mine)
                return true;

            _instanceLock.Dispose();
            _instanceLock = null;
        }
        catch (Exception ex)
        {
            // An unusable mutex is no reason to refuse to run — worst case we're back to the old
            // behaviour of allowing a second copy.
            ErrorLog.Write("single-instance check", ex);
            return true;
        }

        MessageBox.Show(
            "Slide Colours is already running.\n\nLook for the floating pill — it may be behind "
            + "another window or on a different screen.",
            "Slide Colours", MessageBoxButton.OK, MessageBoxImage.Information);
        Shutdown();
        return false;
    }

    /// <summary>
    /// Keeps an unexpected failure from taking the lights out with it. Every background loop
    /// already catches and retries; the UI thread had no net at all, so a single stray exception
    /// there ended the process — and the DMX output — mid-service.
    /// </summary>
    private void InstallLastChanceHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLog.Write("UI thread", args.Exception);
            // Carry on. The engine, capture and output all live on their own threads and are
            // unaffected by whatever the UI was in the middle of.
            args.Handled = true;
        };

        // The process is already going down by the time this runs; all we can do is leave a record.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ErrorLog.Write("background thread", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write("unobserved task", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Null when a second instance shut itself down before any of this was built.
        _cts.Cancel();
        _remote?.Dispose();
        _screen?.Dispose();
        _audio?.Dispose();
        _engine?.Dispose();
        _settings?.Save();

        _instanceLock?.ReleaseMutex();
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
