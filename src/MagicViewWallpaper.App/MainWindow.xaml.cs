using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MagicViewWallpaper.App;

public partial class MainWindow : Window
{
    readonly SettingsStore store = new(); readonly EngineService catalog; readonly EngineClient client = new(); readonly ThumbnailService thumbnails; readonly DispatcherTimer timer;
    AppSettings settings; List<WallpaperItem> items = []; WallpaperItem? selected; bool startupEnabled;
    static readonly string[] Filters = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.mp4", "*.webm", "*.mkv", "*.avi", "*.mov", "*.wmv", "*.m4v"];
    public MainWindow()
    {
        InitializeComponent(); settings = store.Load(); catalog = new(store); thumbnails = new(store); BindEvents(); LoadSettings(); Refresh();
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; timer.Tick += async (_, _) => await Tick(); timer.Start();
        if (!string.IsNullOrWhiteSpace(settings.CurrentWallpaper) && File.Exists(settings.CurrentWallpaper)) CurrentName.Text = Path.GetFileNameWithoutExtension(settings.CurrentWallpaper);
        Loaded += async (_, _) => UpdateStatus(await client.EnsureStartedAsync());
    }
    void BindEvents()
    {
        LibraryNav.Click += (_, _) => ShowPage(true); SettingsNav.Click += (_, _) => ShowPage(false); RefreshLibrary.Click += (_, _) => Refresh(); SearchBox.TextChanged += (_, _) => Render();
        ImportFiles.Click += (_, _) => ImportFileList(); ImportFolder.Click += (_, _) => ImportDirectory(); ApplySelected.Click += async (_, _) => await ApplySelectedItem(); NextWallpaper.Click += async (_, _) => await ApplyRandom(); SaveSettings.Click += async (_, _) => await Save();
        StartupToggle.Click += async (_, _) => await SetStartup(StartupToggle.IsChecked == true); StopEngine.Click += async (_, _) => await SendControl("StopWallpaper"); ShutdownEngine.Click += async (_, _) => await SendControl("Shutdown");
        OpenLogs.Click += (_, _) => { if (!File.Exists(store.LogPath)) File.WriteAllText(store.LogPath, ""); Process.Start(new ProcessStartInfo("notepad.exe", store.LogPath) { UseShellExecute = true }); };
        Closed += (_, _) => { timer.Stop(); catalog.Dispose(); };
    }
    void ShowPage(bool library) { LibraryPage.Visibility = library ? Visibility.Visible : Visibility.Collapsed; SettingsPage.Visibility = library ? Visibility.Collapsed : Visibility.Visible; }
    void LoadSettings() { AutoSwitch.IsChecked = settings.AutoSwitchEnabled; IntervalBox.Text = settings.SwitchIntervalSeconds.ToString(); TransitionBox.Text = settings.TransitionDurationMs.ToString(); SelectTag(TransitionModeBox, settings.TransitionMode); SelectTag(FitModeBox, settings.FitMode); SelectTag(FpsBox, settings.VideoFpsLimit.ToString()); LoadMonitors(); }
    void LoadMonitors()
    {
        MonitorBox.Items.Clear();
        MonitorBox.Items.Add(new ComboBoxItem { Content = "全部显示器（分别适配）", Tag = "ALL" });
        MonitorBox.Items.Add(new ComboBoxItem { Content = "主显示器（自动识别）", Tag = "PRIMARY" });
        int index = 1;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            string primary = screen.Primary ? " · 主屏" : "";
            var bounds = EngineService.DisplayBounds(screen);
            MonitorBox.Items.Add(new ComboBoxItem { Content = $"显示器 {index++} · {bounds.Width}×{bounds.Height}{primary}", Tag = screen.DeviceName });
        }
        SelectTag(MonitorBox, settings.TargetMonitor);
    }
    static void SelectTag(ComboBox box, string tag) { box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == tag) ?? box.Items[0]; }
    async Task Save() { settings.AutoSwitchEnabled = AutoSwitch.IsChecked == true; if (int.TryParse(IntervalBox.Text, out int i)) settings.SwitchIntervalSeconds = Math.Max(10, i); if (int.TryParse(TransitionBox.Text, out int t)) settings.TransitionDurationMs = Math.Clamp(t, 0, 5000); settings.TransitionMode = ((ComboBoxItem)TransitionModeBox.SelectedItem).Tag!.ToString()!; settings.FitMode = ((ComboBoxItem)FitModeBox.SelectedItem).Tag!.ToString()!; settings.VideoFpsLimit = int.Parse(((ComboBoxItem)FpsBox.SelectedItem).Tag!.ToString()!); settings.TargetMonitor = ((ComboBoxItem)MonitorBox.SelectedItem).Tag!.ToString()!; store.Save(settings); var reply = await client.SendAsync("ReloadSettings"); UpdateStatus(reply); ModernDialog.Show(this, reply.Success ? "设置已保存" : "保存失败", reply.Success ? "新的播放与切换设置将在下一次切换时生效。" : reply.Message, reply.Success ? DialogKind.Success : DialogKind.Error); }
    void Refresh() { items = catalog.Scan(settings).ToList(); LibrarySummary.Text = $"{items.Count} 张壁纸 · {items.Count(x => x.IsVideo)} 个动态 · {items.Count(x => !x.IsVideo)} 张静态"; Render(); }
    void Render()
    {
        WallpaperList.Items.Clear(); string q = SearchBox.Text.Trim();
        foreach (var item in items.Where(x => q.Length == 0 || x.Name.Contains(q, StringComparison.OrdinalIgnoreCase))) WallpaperList.Items.Add(Card(item));
    }
    Border Card(WallpaperItem item)
    {
        var border = new Border { Width = 218, Height = 154, Margin = new Thickness(0, 0, 14, 14), CornerRadius = new CornerRadius(13), Background = Brush("#172228"), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        if (!item.IsVideo) { try { var bmp = new BitmapImage(); bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.DecodePixelWidth = 430; bmp.UriSource = new Uri(item.Path); bmp.EndInit(); grid.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill }; } catch { } }
        else { grid.Background = new LinearGradientBrush(ColorOf("#174D52"), ColorOf("#102128"), 35); grid.Children.Add(new TextBlock { Text = "▶", FontSize = 30, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }); _ = LoadVideoThumbnail(grid, item); }
        var footer = new Border { Background = new SolidColorBrush(Color.FromArgb(225, 12, 18, 22)), Padding = new Thickness(12, 7, 10, 6) }; Grid.SetRow(footer, 1); var fg = new Grid(); fg.ColumnDefinitions.Add(new ColumnDefinition()); fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); fg.Children.Add(new TextBlock { Text = item.Name, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis }); var type = new TextBlock { Text = item.TypeLabel, Foreground = Brush("#E6FF59"), FontSize = 11 }; Grid.SetColumn(type, 1); fg.Children.Add(type); footer.Child = fg; grid.Children.Add(footer); border.Child = grid;
        border.MouseLeftButtonUp += (_, _) => { selected = item; foreach (Border b in WallpaperList.Items) b.BorderBrush = Brushes.Transparent; border.BorderBrush = Brush("#E6FF59"); CurrentName.Text = item.Name; };
        border.MouseDown += async (_, e) => { if (e.ClickCount == 2) { selected = item; await ApplySelectedItem(); } }; return border;
    }
    static SolidColorBrush Brush(string hex) => new(ColorOf(hex)); static Color ColorOf(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    async Task LoadVideoThumbnail(Grid grid, WallpaperItem item) { var path = await thumbnails.GetOrCreateAsync(item); if (path == null || !File.Exists(path)) return; await Dispatcher.InvokeAsync(() => { try { var bmp = new BitmapImage(); bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.DecodePixelWidth = 430; bmp.UriSource = new Uri(path); bmp.EndInit(); grid.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill }; } catch { } }); }
    async Task ApplySelectedItem() { if (selected == null) { ModernDialog.Show(this, "尚未选择壁纸", "请先在壁纸库中选择一张壁纸，再点击“应用所选”。", DialogKind.Warning); return; } await Apply(selected); }
    async Task ApplyRandom() { var started = await client.EnsureStartedAsync(); if (!started.Success) { ShowEngineError(started); return; } var reply = await client.SendAsync("Next", timeoutMs: 12000); UpdateStatus(reply); if (!reply.Success) ShowEngineError(reply); }
    async Task Apply(WallpaperItem item) { EngineStatus.Text = "正在加载…"; var started = await client.EnsureStartedAsync(); if (!started.Success) { ShowEngineError(started); return; } var reply = await client.SendAsync("Apply", item.Path, 12000); UpdateStatus(reply); if (reply.Success) CurrentName.Text = item.Name; else ShowEngineError(reply); }
    async Task Tick() { UpdateStatus(await client.SendAsync("Status", timeoutMs: 800)); }
    async Task SetStartup(bool enabled)
    {
        StartupToggle.IsEnabled = false;
        try
        {
            var started = await client.EnsureStartedAsync();
            if (!started.Success) { StartupToggle.IsChecked = startupEnabled; ShowEngineError(started); return; }
            var reply = await client.SendAsync(enabled ? "InstallStartup" : "RemoveStartup", timeoutMs: 8000);
            UpdateStatus(reply);
            if (!reply.Success) { StartupToggle.IsChecked = startupEnabled; ShowEngineError(reply); }
        }
        finally { StartupToggle.IsEnabled = true; }
    }
    async Task SendControl(string action) { var started = action == "Shutdown" ? await client.SendAsync("Status", timeoutMs: 800) : await client.EnsureStartedAsync(); if (!started.Success) { ShowEngineError(started); return; } var reply = await client.SendAsync(action, timeoutMs: 8000); UpdateStatus(reply); if (!reply.Success) ShowEngineError(reply); else if (action is "InstallStartup" or "RemoveStartup") ModernDialog.Show(this, "MagicView Wallpaper", reply.Message, DialogKind.Success); }
    void UpdateStatus(EngineReply reply) { if (reply.Success) { startupEnabled = reply.StartupEnabled; StartupToggle.IsChecked = startupEnabled; } EngineStatus.Text = reply.Running ? (reply.StartupEnabled ? "引擎运行中 · 已开机启动" : "引擎运行中") : "引擎未运行"; StatusDot.Fill = Brush(reply.Running ? "#5AF2A5" : "#607078"); if (!string.IsNullOrWhiteSpace(reply.CurrentWallpaper)) CurrentName.Text = Path.GetFileNameWithoutExtension(reply.CurrentWallpaper); }
    void ShowEngineError(EngineReply reply) => ModernDialog.Show(this, "后台引擎", reply.Message, DialogKind.Error);
    void ImportFileList() { var d = new OpenFileDialog { Multiselect = true, Filter = "壁纸文件|*.jpg;*.jpeg;*.png;*.bmp;*.mp4;*.webm;*.mkv;*.avi;*.mov;*.wmv;*.m4v|所有文件|*.*" }; if (d.ShowDialog() == true) { foreach (var p in d.FileNames) CopyIntoLibrary(p); EnsureLibrary(); Refresh(); } }
    void ImportDirectory() { var d = new OpenFolderDialog { Title = "选择包含壁纸的文件夹" }; if (d.ShowDialog() == true) { foreach (var pattern in Filters) foreach (var p in Directory.EnumerateFiles(d.FolderName, pattern, SearchOption.AllDirectories)) CopyIntoLibrary(p); EnsureLibrary(); Refresh(); } }
    void CopyIntoLibrary(string source) { string dest = Path.Combine(store.LibraryRoot, Path.GetFileName(source)); if (File.Exists(dest)) dest = Path.Combine(store.LibraryRoot, $"{Path.GetFileNameWithoutExtension(source)}_{DateTime.Now:yyyyMMddHHmmssfff}{Path.GetExtension(source)}"); File.Copy(source, dest); }
    void EnsureLibrary() { if (!settings.SourceFolders.Contains(store.LibraryRoot, StringComparer.OrdinalIgnoreCase)) settings.SourceFolders.Insert(0, store.LibraryRoot); store.Save(settings); }
}
