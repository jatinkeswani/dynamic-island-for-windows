using System.Windows;

namespace DynamicIsland;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;
    private TrayIcon? _tray;
    private Settings _settings = new();
    private MainWindow? _island;
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // `DynamicIsland.exe --screenshots <dir>` renders the README screenshots with demo data and exits
        if (e.Args.Length == 2 && e.Args[0] == "--screenshots")
        {
            new MainWindow(new Settings()) { ScreenshotDir = e.Args[1] }.Show();
            return;
        }

        _singleInstance = new Mutex(true, "DynamicIsland.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        _settings = Settings.Load();
        _island = new MainWindow(_settings);
        _island.Show();
        _tray = new TrayIcon(OpenSettings, Quit);
    }

    public void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, () => _island?.ApplySettings());
        _settingsWindow.Show();
    }

    public void Quit() => Shutdown();

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        base.OnExit(e);
    }
}
