using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

/// <summary>
/// Renders README screenshots of each island state with demo data (run with <c>--screenshots &lt;dir&gt;</c>).
/// Nothing from the real screen, media session or audio devices is captured.
/// </summary>
public partial class MainWindow
{
    private const double ScreenshotScale = 2;

    private void RenderScreenshots(string dir)
    {
        Directory.CreateDirectory(dir);

        // Demo data
        ApplyMedia(new MediaState("Midnight City", "M83", DemoArtwork(), true, true, true, true, "Spotify.exe"));
        OutputList.ItemsSource = new List<AudioDevice>
        {
            new("1", "Speakers (Realtek Audio)", true),
            new("2", "Headphones (AirPods Pro)", false),
            new("3", "Monitor (HDMI)", false),
        };
        _shelf.Add(new ShelfItem { Path = "Screenshots", Name = "Screenshots" });
        _shelf.Add(new ShelfItem { Path = "Notepad", Name = "notepad.exe", Icon = LoadIcon(@"C:\Windows\notepad.exe") });
        _shelf.Add(new ShelfItem { Path = "Registry", Name = "regedit.exe", Icon = LoadIcon(@"C:\Windows\regedit.exe") });

        Pose(IslandState.Hidden, 0, TabWidth, TabHeight);
        Save(dir, "1-hidden-tab.png", 60);

        // A frame from the reveal: the drop pinching off the edge
        Pose(IslandState.Compact, 4, 34, 32);
        Save(dir, "2-droplet.png", 80);

        Pose(IslandState.Compact, FloatGap, CompactWidth, CompactHeight);
        Save(dir, "3-compact-music.png", 80);

        Pose(IslandState.Expanded, FloatGap, ExpandedWidth, ExpandedHeight);
        Save(dir, "4-expanded.png", WindowHeight);

        // Timer running while music plays: the timer takes the pill, music splits into the bubble
        _timer.SetDuration(TimeSpan.FromMinutes(25));
        _timer.Start();
        _timer.Pause();
        _timerDone = true; // keeps the timer as the pill's activity for the shot
        UpdateTimer();
        CompactTimerText.Text = "24:59";
        Pose(IslandState.Compact, FloatGap, CompactWidth, CompactHeight, bubble: 1);
        Save(dir, "5-timer-and-music.png", 80);

        _alertMode = true;
        AlertDetail.Text = "05:00 timer · click to dismiss";
        Pose(IslandState.Alert, FloatGap, AlertWidth, AlertHeight);
        Save(dir, "6-timer-alert.png", 130);
    }

    private void Pose(IslandState state, double top, double width, double height, double bubble = 0)
    {
        _state = state;
        _top.Snap(top);
        _width.Snap(width);
        _height.Snap(height);
        _bubble.Snap(bubble);
        _scale.Snap(1);
        ApplyFrame();
        UpdateLayout();
    }

    // The island over a wallpaper-like gradient, cropped to the given height, at 2x
    private void Save(string dir, string name, double cropHeight)
    {
        int width = (int)(WindowWidth * ScreenshotScale);
        int height = (int)(cropHeight * ScreenshotScale);

        var island = new RenderTargetBitmap(width, (int)(WindowHeight * ScreenshotScale), 96 * ScreenshotScale, 96 * ScreenshotScale, PixelFormats.Pbgra32);
        island.Render(Root);

        var backdrop = new LinearGradientBrush(Color.FromRgb(0x5B, 0x7C, 0xC9), Color.FromRgb(0xB0, 0x8B, 0xD8), 45);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(backdrop, null, new Rect(0, 0, WindowWidth, cropHeight));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, WindowWidth, cropHeight)));
            dc.DrawImage(island, new Rect(0, 0, WindowWidth, WindowHeight));
            dc.Pop();
        }

        var output = new RenderTargetBitmap(width, height, 96 * ScreenshotScale, 96 * ScreenshotScale, PixelFormats.Pbgra32);
        output.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(output));
        using var file = File.Create(Path.Combine(dir, name));
        encoder.Save(file);
    }

    // Generated album art: a warm gradient with a soft sun
    private static byte[] DemoArtwork()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xFF, 0x5F, 0x6D), Color.FromRgb(0x3A, 0x1C, 0x71), 90), null, new Rect(0, 0, 200, 200));
            dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(0xEE, 0xFF, 0xC3, 0x71), Color.FromArgb(0, 0xFF, 0xC3, 0x71)), null, new Point(100, 110), 70, 70);
        }
        var bitmap = new RenderTargetBitmap(200, 200, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
