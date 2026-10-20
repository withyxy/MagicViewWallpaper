using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MagicViewWallpaper.App;

public sealed class EngineService : IDisposable
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".webm", ".mkv", ".avi", ".mov", ".wmv", ".m4v" };
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };
    readonly SettingsStore store;
    sealed class Surface { public required string DeviceName { get; init; } public required Process Player { get; init; } public required IntPtr Canvas { get; init; } public required System.Drawing.Rectangle DisplayBounds { get; init; } public required System.Drawing.Rectangle Area { get; set; } }
    List<Surface> surfaces = [];
    public bool IsRunning => surfaces.Count > 0 && surfaces.All(x => !x.Player.HasExited && Native.IsWindow(x.Canvas));

    public EngineService(SettingsStore store) => this.store = store;

    public IReadOnlyList<WallpaperItem> Scan(AppSettings settings) => settings.SourceFolders
        .Where(Directory.Exists).SelectMany(p => Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
        .Where(p => VideoExtensions.Contains(Path.GetExtension(p)) || ImageExtensions.Contains(Path.GetExtension(p)))
        .Distinct(StringComparer.OrdinalIgnoreCase).Select(p => new WallpaperItem { Path = p }).OrderBy(x => x.Name).ToList();

    public async Task ApplyAsync(WallpaperItem item, AppSettings settings)
    {
        var mpv = Path.Combine(AppContext.BaseDirectory, "Runtime", "mpv", "mpv.exe");
        if (!File.Exists(mpv)) throw new FileNotFoundException("缺少 MPV 运行库。", mpv);
        var targets = TargetScreens(settings.TargetMonitor);
        var nextSurfaces = new List<Surface>();
        try
        {
            foreach (var screen in targets)
            {
                var displayBounds = EffectiveBounds(screen);
                store.Log($"显示器布局 {screen.DeviceName}: Bounds={screen.Bounds}, WorkingArea={screen.WorkingArea}, Effective={displayBounds}, Primary={screen.Primary}");
                var nextCanvas = Native.CreateCanvas(displayBounds);
                if (nextCanvas == IntPtr.Zero) throw new InvalidOperationException($"{screen.DeviceName}: {Native.LastErrorMessage}");
                store.Log($"画布布局 {screen.DeviceName}: {Native.LastLayoutMessage}");
                var canvasArea = Native.CanvasArea(displayBounds);
                if (canvasArea.Width <= 0 || canvasArea.Height <= 0) throw new InvalidOperationException($"{screen.DeviceName}: 无法取得壁纸画布区域。");
                var args = PlayerArguments(nextCanvas, item, settings);
                var psi = new ProcessStartInfo(mpv) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(mpv)! };
                foreach (var argument in args) psi.ArgumentList.Add(argument);
                var next = Process.Start(psi) ?? throw new InvalidOperationException("MPV 启动失败。");
                nextSurfaces.Add(new Surface { DeviceName = screen.DeviceName, Player = next, Canvas = nextCanvas, DisplayBounds = displayBounds, Area = canvasArea });
            }
            await Task.Delay(item.IsVideo ? 700 : 1000);
            var failed = nextSurfaces.FirstOrDefault(x => x.Player.HasExited);
            if (failed != null) throw new InvalidOperationException($"{failed.DeviceName} 的 MPV 启动失败，退出码 {failed.Player.ExitCode}。");
            await RunTransition(nextSurfaces, settings);
            StopCurrent();
            // Keep the wallpaper as a sibling immediately behind the Magic View
            // desktop host. A child canvas always covers pixels drawn by its parent,
            // including Magic View's own desktop icons.
            foreach (var nextSurface in nextSurfaces) Native.SendBehindDesktop(nextSurface.Canvas, nextSurface.DisplayBounds);
            surfaces = nextSurfaces; nextSurfaces = []; settings.CurrentWallpaper = item.Path; store.Save(settings); store.Log($"应用 {item.TypeLabel}壁纸到 {string.Join(", ", surfaces.Select(x => x.DeviceName))}：{item.Path}");
        }
        finally { Stop(nextSurfaces); }
    }

    async Task RunTransition(List<Surface> nextSurfaces, AppSettings settings)
    {
        int duration = Math.Clamp(settings.TransitionDurationMs, 0, 5000);
        string mode = settings.TransitionMode is "SlideLeft" or "SlideRight" or "Direct" ? settings.TransitionMode : "Fade";

        foreach (var next in nextSurfaces)
        {
            var old = surfaces.FirstOrDefault(x => x.DeviceName.Equals(next.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (old != null) Native.PlaceAbove(next.Canvas, old.Canvas);
        }

        if (mode == "Direct" || duration == 0)
        {
            foreach (var next in nextSurfaces) Native.SetOpacity(next.Canvas, 255);
            return;
        }

        int steps = Math.Clamp(duration / 16, 12, 90);
        if (mode is "SlideLeft" or "SlideRight")
        {
            int direction = mode == "SlideLeft" ? 1 : -1;
            foreach (var next in nextSurfaces)
            {
                Native.MoveCanvas(next.Canvas, next.Area.X + direction * next.Area.Width, next.Area.Y, next.Area.Width, next.Area.Height);
                Native.SetOpacity(next.Canvas, 255);
            }

            for (int i = 0; i <= steps; i++)
            {
                double progress = (double)i / steps;
                double eased = 1 - Math.Pow(1 - progress, 3);
                foreach (var next in nextSurfaces)
                {
                    int offset = (int)Math.Round(direction * next.Area.Width * (1 - eased));
                    Native.MoveCanvas(next.Canvas, next.Area.X + offset, next.Area.Y, next.Area.Width, next.Area.Height);
                    var old = surfaces.FirstOrDefault(x => x.DeviceName.Equals(next.DeviceName, StringComparison.OrdinalIgnoreCase));
                    if (old != null)
                    {
                        int oldOffset = (int)Math.Round(-direction * old.Area.Width * eased);
                        Native.MoveCanvas(old.Canvas, old.Area.X + oldOffset, old.Area.Y, old.Area.Width, old.Area.Height);
                    }
                }
                await Task.Delay(Math.Max(1, duration / steps));
            }
            return;
        }

        // The new canvas must be above the old canvas. Earlier builds placed it
        // behind the opaque old surface, making this animation look instantaneous.
        for (int i = 0; i <= steps; i++)
        {
            double progress = (double)i / steps;
            double eased = progress * progress * (3 - 2 * progress);
            foreach (var next in nextSurfaces) Native.SetOpacity(next.Canvas, (byte)Math.Round(255 * eased));
            await Task.Delay(Math.Max(1, duration / steps));
        }
    }

    static IReadOnlyList<System.Windows.Forms.Screen> TargetScreens(string target)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (target.Equals("ALL", StringComparison.OrdinalIgnoreCase)) return screens;
        if (target.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)) return [System.Windows.Forms.Screen.PrimaryScreen ?? screens[0]];
        return [screens.FirstOrDefault(x => x.DeviceName.Equals(target, StringComparison.OrdinalIgnoreCase)) ?? System.Windows.Forms.Screen.PrimaryScreen ?? screens[0]];
    }

    public static System.Drawing.Rectangle EffectiveBounds(System.Windows.Forms.Screen screen)
    {
        var bounds = screen.Bounds;
        var work = screen.WorkingArea;
        if (work.Width <= 0 || work.Height <= 0) return bounds;
        double scaleX = (double)bounds.Width / work.Width;
        double scaleY = (double)bounds.Height / work.Height;
        // Some mixed-DPI Magic View configurations expose physical pixels in
        // Bounds but logical pixels in WorkingArea. A normal taskbar only changes
        // one edge slightly; a matching 125%+ ratio on both axes is DPI scaling.
        if (scaleX >= 1.25 && scaleY >= 1.25 && Math.Abs(scaleX - scaleY) < 0.08)
            return work;
        return bounds;
    }

    public static System.Drawing.Rectangle DisplayBounds(System.Windows.Forms.Screen screen)
    {
        var mapped = Native.CanvasArea(screen.Bounds);
        return mapped.Width > 0 && mapped.Height > 0
            ? new System.Drawing.Rectangle(screen.Bounds.X, screen.Bounds.Y, mapped.Width, mapped.Height)
            : EffectiveBounds(screen);
    }
    static List<string> PlayerArguments(IntPtr canvas, WallpaperItem item, AppSettings settings)
    {
        var args = new List<string> { $"--wid={canvas}", "--loop-file=inf", "--hwdec=d3d11va", "--vo=gpu", "--gpu-api=d3d11", "--deband=no", "--video-sync=display-resample", "--interpolation=no", "--framedrop=no", "--osc=no", "--input-default-bindings=no" };
        if (settings.MuteVideo) args.Add("--no-audio"); args.Add("--pause=no");
        if (!item.IsVideo) args.Add("--image-display-duration=inf");
        if (settings.FitMode == "Cover") args.Add("--panscan=1.0");
        args.Add($"--scale={settings.ScaleQuality}"); args.Add($"--cscale={settings.ScaleQuality}"); args.Add($"--dscale={settings.ScaleQuality}");
        if (item.IsVideo && settings.VideoFpsLimit > 0) args.Add($"--vf=fps={settings.VideoFpsLimit}");
        args.Add("--"); args.Add(item.Path); return args;
    }

    public void Resize()
    {
        foreach (var surface in surfaces)
        {
            var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(x => x.DeviceName.Equals(surface.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (screen == null) continue;
            var bounds = EffectiveBounds(screen);
            Native.ResizeCanvas(surface.Canvas, bounds);
            var area = Native.CanvasArea(bounds);
            if (area.Width > 0 && area.Height > 0) surface.Area = area;
        }
    }
    static void Stop(IEnumerable<Surface> values) { foreach (var surface in values) { try { if (!surface.Player.HasExited) surface.Player.Kill(true); } catch { } surface.Player.Dispose(); if (surface.Canvas != IntPtr.Zero) Native.DestroyWindow(surface.Canvas); } }
    public void StopCurrent() { Stop(surfaces); surfaces = []; }
    public void Dispose() => StopCurrent();

    static class Native
    {
        const int GWLP_WNDPROC = -4, WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021;
        static readonly WndProc CanvasWindowProcDelegate = CanvasWindowProc;
        static readonly Dictionary<IntPtr, IntPtr> OriginalWindowProcedures = [];
        delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
        const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
        delegate bool EnumWindowProc(IntPtr hwnd, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string c, string? t);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string? t);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder className, int maximum);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO i);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool EnableWindow(IntPtr h, bool enable);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
        [DllImport("user32.dll")] static extern IntPtr CallWindowProc(IntPtr previous, IntPtr h, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        static IntPtr Host()
        {
            // Fast path for the hierarchy used by most Magic View releases.
            var progman = FindWindow("Progman", null);
            var direct = progman == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(progman, IntPtr.Zero, "HonorDesktop_HideWnd_Class", null);
            if (IsUsableHost(direct)) return direct;

            // Some releases create more than one Progman or move the host below WorkerW.
            // Enumerate the complete desktop tree and require a real card mount child so
            // the zero-sized HonorDesktop_HideWnd_Class notification window is rejected.
            IntPtr result = IntPtr.Zero;
            bool Inspect(IntPtr hwnd, IntPtr _)
            {
                if (IsUsableHost(hwnd)) { result = hwnd; return false; }
                EnumChildWindows(hwnd, (child, __) => { if (IsUsableHost(child)) { result = child; return false; } return true; }, IntPtr.Zero);
                return result == IntPtr.Zero;
            }
            EnumWindows(Inspect, IntPtr.Zero);
            return result;
        }
        static bool IsUsableHost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
            var name = new System.Text.StringBuilder(128);
            if (GetClassName(hwnd, name, name.Capacity) == 0 || name.ToString() != "HonorDesktop_HideWnd_Class") return false;
            if (!GetClientRect(hwnd, out var rect) || rect.R - rect.L < 100 || rect.B - rect.T < 100) return false;
            return FindWindowEx(hwnd, IntPtr.Zero, "NewExplorerCardMountHost", null) != IntPtr.Zero;
        }
        static bool IsHostCandidate(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
            var name = new System.Text.StringBuilder(128);
            return GetClassName(hwnd, name, name.Capacity) > 0 && name.ToString() == "HonorDesktop_HideWnd_Class" && GetClientRect(hwnd, out var r) && r.R - r.L >= 100 && r.B - r.T >= 100;
        }
        static IntPtr HostFor(System.Drawing.Rectangle bounds)
        {
            var candidates = new HashSet<IntPtr>();
            void AddTree(IntPtr root) { if (IsHostCandidate(root)) candidates.Add(root); EnumChildWindows(root, (child, _) => { if (IsHostCandidate(child)) candidates.Add(child); return true; }, IntPtr.Zero); }
            EnumWindows((window, _) => { AddTree(window); return true; }, IntPtr.Zero);
            IntPtr best = IntPtr.Zero; long bestArea = 0;
            foreach (var candidate in candidates)
            {
                if (!GetClientRect(candidate, out var r)) continue; var origin = new POINT(0, 0); if (!ClientToScreen(candidate, ref origin)) continue;
                var hostBounds = new System.Drawing.Rectangle(origin.X, origin.Y, r.R - r.L, r.B - r.T);
                var intersection = System.Drawing.Rectangle.Intersect(hostBounds, bounds); long area = (long)intersection.Width * intersection.Height;
                if (area > bestArea) { bestArea = area; best = candidate; }
            }
            return best != IntPtr.Zero ? best : Host();
        }
        static bool Area(IntPtr host, System.Drawing.Rectangle bounds, out int x, out int y, out int w, out int h)
        {
            var topLeft = new POINT(bounds.Left, bounds.Top);
            var bottomRight = new POINT(bounds.Right, bounds.Bottom);
            if (!ScreenToClient(host, ref topLeft) || !ScreenToClient(host, ref bottomRight))
            {
                x = y = w = h = 0;
                return false;
            }
            x = topLeft.X;
            y = topLeft.Y;
            w = bottomRight.X - topLeft.X;
            h = bottomRight.Y - topLeft.Y;
            return w > 0 && h > 0;
        }
        static bool DesktopLayer(System.Drawing.Rectangle bounds, out IntPtr host, out IntPtr parent, out int x, out int y, out int w, out int h)
        {
            host = HostFor(bounds);
            parent = host == IntPtr.Zero ? IntPtr.Zero : GetParent(host);
            if (parent == IntPtr.Zero) { x = y = w = h = 0; return false; }
            return Area(parent, bounds, out x, out y, out w, out h);
        }
        public static string LastErrorMessage { get; private set; } = "Magic 视界壁纸画布创建失败。";
        public static string LastLayoutMessage { get; private set; } = "尚未创建画布。";
        public static System.Drawing.Rectangle CanvasArea(System.Drawing.Rectangle bounds)
        {
            return DesktopLayer(bounds, out _, out _, out int x, out int y, out int w, out int h)
                ? new System.Drawing.Rectangle(x, y, w, h)
                : System.Drawing.Rectangle.Empty;
        }
        public static IntPtr CreateCanvas(System.Drawing.Rectangle bounds) { if (!DesktopLayer(bounds, out var host, out var parent, out int x, out int y, out int w, out int h)) { LastErrorMessage = "未找到可挂载的 Magic 视界桌面层，请确认 Magic 视界已开启。"; return IntPtr.Zero; } LastLayoutMessage = $"Host=0x{host.ToInt64():X}, Parent=0x{parent.ToInt64():X}, Screen={bounds}, Sibling=({x},{y},{w},{h})"; var c = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE, "Static", "MagicViewWallpaperCanvas", WS_CHILD | WS_VISIBLE, x, y, w, h, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); if (c == IntPtr.Zero) { int error = Marshal.GetLastWin32Error(); LastErrorMessage = $"已找到 Magic 视界宿主，但无法创建壁纸画布（Win32 错误 {error}）。"; return IntPtr.Zero; } var original = SetWindowLongPtr(c, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(CanvasWindowProcDelegate)); if (original != IntPtr.Zero) OriginalWindowProcedures[c] = original; EnableWindow(c, false); SetOpacity(c, 0); SetWindowPos(c, host, x, y, w, h, 0x50); return c; }
        static IntPtr CanvasWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WM_NCHITTEST) return new IntPtr(-1); // HTTRANSPARENT
            if (message == WM_MOUSEACTIVATE) return new IntPtr(3); // MA_NOACTIVATE
            return OriginalWindowProcedures.TryGetValue(hwnd, out var previous)
                ? CallWindowProc(previous, hwnd, message, wParam, lParam)
                : IntPtr.Zero;
        }
        public static void ResizeCanvas(IntPtr c, System.Drawing.Rectangle bounds)
        {
            var host = GetParent(c);
            if (host == IntPtr.Zero || !Area(host, bounds, out int x, out int y, out int w, out int h)) return;
            if (!GetClientRect(c, out var client) || !GetWindowRect(c, out var window)) return;
            var current = new POINT(window.L, window.T);
            if (!ScreenToClient(host, ref current)) return;
            if (current.X == x && current.Y == y && client.R - client.L == w && client.B - client.T == h) return;
            // Preserve the established layer. Re-inserting at HWND_BOTTOM every
            // second exposes the original Magic View wallpaper during repaint.
            SetWindowPos(c, IntPtr.Zero, x, y, w, h, 0x14);
        }
        public static void SetOpacity(IntPtr c, byte a) => SetLayeredWindowAttributes(c, 0, a, 2);
        public static void MoveCanvas(IntPtr c, int x, int y, int w, int h) => SetWindowPos(c, IntPtr.Zero, x, y, w, h, 0x14);
        public static void PlaceAbove(IntPtr c, IntPtr lower) => SetWindowPos(lower, c, 0, 0, 0, 0, 0x53);
        public static void SendBehindDesktop(IntPtr c, System.Drawing.Rectangle bounds)
        {
            var host = HostFor(bounds);
            if (host != IntPtr.Zero && GetParent(host) == GetParent(c)) SetWindowPos(c, host, 0, 0, 0, 0, 0x53);
        }
    }
}
