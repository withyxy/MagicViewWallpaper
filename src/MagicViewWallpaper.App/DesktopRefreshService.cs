using System.Runtime.InteropServices;

namespace MagicViewWallpaper.App;

/// <summary>
/// Keeps Explorer's desktop icon view in sync while Magic View owns the
/// visible desktop host. Some Magic View builds do not forward shell change
/// notifications to the icon view after a custom wallpaper child is added.
/// </summary>
public sealed class DesktopRefreshService : IDisposable
{
    const uint SHCNE_UPDATEDIR = 0x00001000;
    const uint SHCNF_PATHW = 0x0005;
    const uint SHCNF_FLUSHNOWAIT = 0x2000;
    const uint WM_COMMAND = 0x0111;
    const uint RefreshCommand = 0x7103;
    const uint SMTO_ABORTIFHUNG = 0x0002;

    readonly List<FileSystemWatcher> watchers = [];
    readonly System.Threading.Timer debounceTimer;
    readonly string[] desktopPaths;
    bool disposed;

    public DesktopRefreshService(SettingsStore store)
    {
        desktopPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        }
        .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        debounceTimer = new System.Threading.Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);

        foreach (var path in desktopPaths)
        {
            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime
                };
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += (_, e) => store.Log("桌面目录监听错误：" + e.GetException().Message);
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception ex) { store.Log($"无法监听桌面目录 {path}：{ex.Message}"); }
        }

        RequestRefresh();
    }

    void OnChanged(object sender, FileSystemEventArgs e) => RequestRefresh();
    void OnRenamed(object sender, RenamedEventArgs e) => RequestRefresh();

    public void RequestRefresh()
    {
        if (!disposed) debounceTimer.Change(250, Timeout.Infinite);
    }

    void Refresh()
    {
        if (disposed) return;
        foreach (var path in desktopPaths)
            SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, path, null);

        var desktopView = FindDesktopView();
        if (desktopView != IntPtr.Zero)
            SendMessageTimeout(desktopView, WM_COMMAND, new IntPtr(RefreshCommand), IntPtr.Zero,
                SMTO_ABORTIFHUNG, 1000, out _);
    }

    static IntPtr FindDesktopView()
    {
        var progman = FindWindow("Progman", null);
        var view = progman == IntPtr.Zero
            ? IntPtr.Zero
            : FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view != IntPtr.Zero) return view;

        IntPtr worker = IntPtr.Zero;
        while ((worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
        {
            view = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view != IntPtr.Zero) return view;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var watcher in watchers) watcher.Dispose();
        debounceTimer.Dispose();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotify(uint eventId, uint flags, string? item1, string? item2);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
}
