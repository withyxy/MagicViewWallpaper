using System.Text.Json;

namespace MagicViewWallpaper.App;

public sealed class AppSettings
{
    public List<string> SourceFolders { get; set; } = [];
    public bool AutoSwitchEnabled { get; set; } = true;
    public int SwitchIntervalSeconds { get; set; } = 300;
    public int TransitionDurationMs { get; set; } = 900;
    public string FitMode { get; set; } = "Cover";
    public string TargetMonitor { get; set; } = "PRIMARY";
    public int VideoFpsLimit { get; set; }
    public string ScaleQuality { get; set; } = "bilinear";
    public bool MuteVideo { get; set; } = true;
    public string? CurrentWallpaper { get; set; }
}

public sealed class WallpaperItem
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
    public bool IsVideo => EngineService.VideoExtensions.Contains(System.IO.Path.GetExtension(Path));
    public string TypeLabel => IsVideo ? "动态" : "静态";
}

public sealed class SettingsStore
{
    public string DataRoot { get; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagicViewWallpaper");
    public string LibraryRoot => System.IO.Path.Combine(DataRoot, "Library");
    public string SettingsPath => System.IO.Path.Combine(DataRoot, "settings.json");
    public string LogPath => System.IO.Path.Combine(DataRoot, "app.log");

    public SettingsStore() { Directory.CreateDirectory(LibraryRoot); }

    public AppSettings Load()
    {
        try { if (File.Exists(SettingsPath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? New(); }
        catch { }
        return New();
    }

    AppSettings New() => new() { SourceFolders = [LibraryRoot, @"D:\Wallpapers"] };
    public void Save(AppSettings settings) => File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    public void Log(string message) => File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
}
