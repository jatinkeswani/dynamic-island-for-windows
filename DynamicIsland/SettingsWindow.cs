using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DynamicIsland;

/// <summary>Settings window opened from the tray. Every change applies and saves immediately.</summary>
public sealed class SettingsWindow : Window
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DynamicIsland";

    private readonly Settings _settings;
    private readonly Action _apply;
    private readonly StackPanel _panel = new() { Margin = new Thickness(20) };

    public SettingsWindow(Settings settings, Action apply)
    {
        _settings = settings;
        _apply = apply;
        Title = "Dynamic Island Settings";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = _panel;
        Build();
    }

    private void Build()
    {
        _panel.Children.Clear();

        _panel.Children.Add(Label("Display"));
        var monitors = new ComboBox { Margin = new Thickness(0, 0, 0, 14) };
        monitors.Items.Add("Primary display");
        var screens = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
            monitors.Items.Add($"Display {i + 1}{(screens[i].Primary ? " (primary)" : "")}  {screens[i].Bounds.Width}×{screens[i].Bounds.Height}");
        monitors.SelectedIndex = Math.Clamp(_settings.MonitorIndex + 1, 0, screens.Length);
        monitors.SelectionChanged += (_, _) => Change(() => _settings.MonitorIndex = monitors.SelectedIndex - 1);
        _panel.Children.Add(monitors);

        AddSlider("Horizontal position (from center)", -1200, 1200, 10, () => _settings.OffsetX, v => _settings.OffsetX = v, "{0:+0;-0;0} px");
        AddSlider("Activation area width", 40, 300, 5, () => _settings.ActivationWidth, v => _settings.ActivationWidth = v, "{0:0} px");
        AddSlider("Activation area height", 2, 30, 1, () => _settings.ActivationHeight, v => _settings.ActivationHeight = v, "{0:0} px");
        AddSlider("Safe margin around island", 0, 200, 5, () => _settings.SafeMargin, v => _settings.SafeMargin = v, "{0:0} px");
        AddSlider("Hide delay", 100, 3000, 50, () => _settings.HideDelayMs, v => _settings.HideDelayMs = (int)v, "{0:0} ms");
        AddSlider("Reveal animation", 250, 700, 10, () => _settings.RevealMs, v => _settings.RevealMs = (int)v, "{0:0} ms");

        var fullscreen = new CheckBox { Content = "Hide while a fullscreen app or game is running", IsChecked = _settings.HideInFullscreen, Margin = new Thickness(0, 4, 0, 8) };
        fullscreen.Click += (_, _) => Change(() => _settings.HideInFullscreen = fullscreen.IsChecked == true);
        _panel.Children.Add(fullscreen);

        var startup = new CheckBox { Content = "Start with Windows", IsChecked = IsStartupEnabled(), Margin = new Thickness(0, 0, 0, 16) };
        startup.Click += (_, _) => SetStartup(startup.IsChecked == true);
        _panel.Children.Add(startup);

        // Picture-in-picture needs the companion extension loaded once in Chrome or Edge
        _panel.Children.Add(new TextBlock { Text = "Video picture-in-picture (Chrome / Edge)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        _panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = System.Windows.Media.Brushes.DimGray,
            Text = "1. Open chrome://extensions (or edge://extensions) and turn on Developer mode.\n" +
                   "2. Click \"Load unpacked\" and choose the folder opened by the button below.\n" +
                   "3. Open chrome://extensions/shortcuts and check that \"Open the playing video in picture-in-picture\" is Ctrl+Shift+9 and set to Global.",
        });
        var extension = new Button { Content = "Open extension folder", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 0, 16) };
        extension.Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", $"\"{System.IO.Path.Combine(AppContext.BaseDirectory, "BrowserExtension")}\"");
        _panel.Children.Add(extension);

        var reset = new Button { Content = "Reset to defaults", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 4, 12, 4) };
        reset.Click += (_, _) =>
        {
            Change(() => _settings.CopyFrom(new Settings()));
            Build();
        };
        _panel.Children.Add(reset);
    }

    private void AddSlider(string label, double min, double max, double step, Func<double> get, Action<double> set, string format)
    {
        var header = new DockPanel();
        var value = new TextBlock { Text = string.Format(format, get()), Foreground = System.Windows.Media.Brushes.Gray };
        DockPanel.SetDock(value, Dock.Right);
        header.Children.Add(value);
        header.Children.Add(Label(label));
        _panel.Children.Add(header);

        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = get(),
            TickFrequency = step, IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 0, 0, 12),
        };
        slider.ValueChanged += (_, e) =>
        {
            value.Text = string.Format(format, e.NewValue);
            Change(() => set(e.NewValue));
        };
        _panel.Children.Add(slider);
    }

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 0, 0, 4) };

    private void Change(Action change)
    {
        change();
        _settings.Save();
        _apply();
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) != null;
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunValue, false);
    }
}
