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
    private UpdateService _update = null!;

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

        _update = new UpdateService();

        MainWindow = new MainWindow(_settings, _engine, _client, _update);
        MainWindow.Show();

        // Check for a new version in the background; the window shows a dot on the settings cog
        // if one is found. Never blocks startup and never surfaces errors here.
        _ = _update.CheckAsync(_cts.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts.Cancel();
        _engine.Dispose();
        _settings.Save();
        base.OnExit(e);
    }
}
