using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DynamicIsland;

/// <summary>System tray icon with Settings and Quit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(Action openSettings, Action quit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings…", null, (_, _) => openSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Dynamic Island", null, (_, _) => quit());

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Dynamic Island",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => openSettings();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    // A white pill, readable on both light and dark taskbars
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        using (var path = new GraphicsPath())
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            path.AddArc(4, 10, 12, 12, 90, 180);
            path.AddArc(16, 10, 12, 12, 270, 180);
            path.CloseFigure();
            g.FillPath(Brushes.White, path);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
