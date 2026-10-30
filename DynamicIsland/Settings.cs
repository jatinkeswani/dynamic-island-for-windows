using System.IO;
using System.Text.Json;

namespace DynamicIsland;

/// <summary>User-adjustable behaviour, saved to %APPDATA%\DynamicIsland\settings.json. Sizes are in DIPs.</summary>
public sealed class Settings
{
    /// <summary>Index into the display list; -1 means the primary display.</summary>
    public int MonitorIndex { get; set; } = -1;
    /// <summary>Horizontal offset of the island from the display's center (positive = right).</summary>
    public double OffsetX { get; set; } = 0;
    public double ActivationWidth { get; set; } = 90;
    public double ActivationHeight { get; set; } = 8;
    public double SafeMargin { get; set; } = 60;
    public int HideDelayMs { get; set; } = 500;
    public int RevealMs { get; set; } = 400;
    public bool HideInFullscreen { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception)
        {
            // A corrupt file falls back to defaults
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Settings still apply for this session
        }
    }

    public void CopyFrom(Settings other)
    {
        MonitorIndex = other.MonitorIndex;
        OffsetX = other.OffsetX;
        ActivationWidth = other.ActivationWidth;
        ActivationHeight = other.ActivationHeight;
        SafeMargin = other.SafeMargin;
        HideDelayMs = other.HideDelayMs;
        RevealMs = other.RevealMs;
        HideInFullscreen = other.HideInFullscreen;
    }
}
