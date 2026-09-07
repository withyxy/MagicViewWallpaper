using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MagicViewWallpaper.App;

public sealed class EngineHost : System.Windows.Forms.ApplicationContext
{
    const string PipeName = "MagicViewWallpaper.Engine.v1";
    const string TaskName = "MagicViewWallpaperEngine";
    readonly SettingsStore store = new(); readonly EngineService engine; readonly SemaphoreSlim operationGate = new(1, 1);
    readonly CancellationTokenSource shutdown = new(); readonly System.Windows.Forms.NotifyIcon tray; readonly System.Threading.Timer timer; readonly System.Windows.Forms.Timer trayRecoveryTimer; readonly System.Windows.Forms.Control uiDispatcher; readonly System.Drawing.Icon appIcon;
    AppSettings settings; List<WallpaperItem> items = []; DateTime nextSwitch; bool busy; bool exiting; int explorerProcessId;

    public EngineHost()
    {
        settings = store.Load(); engine = new(store); Reload(); nextSwitch = DateTime.Now.AddSeconds(settings.SwitchIntervalSeconds);
        uiDispatcher = new System.Windows.Forms.Control(); _ = uiDispatcher.Handle;
        appIcon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开管理器", null, (_, _) => OpenManager());
        menu.Items.Add("随机下一张", null, async (_, _) => await ApplyNext());
        menu.Items.Add("暂停自动切换", null, (_, _) => ToggleAuto());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出后台引擎", null, (_, _) => ExitEngine());
        tray = new System.Windows.Forms.NotifyIcon { Icon = appIcon, Text = "MagicView Wallpaper 后台引擎", Visible = false, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => OpenManager();
        trayRecoveryTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        trayRecoveryTimer.Tick += (_, _) => RestoreTrayIcon(); trayRecoveryTimer.Start(); RestoreTrayIcon();
        _ = Task.Run(PipeLoop); timer = new System.Threading.Timer(async _ => await TimerTick(), null, 1000, 1000);
        _ = RestoreCurrent(); store.Log("后台引擎已启动。");
    }

    void Reload() { settings = store.Load(); items = engine.Scan(settings).ToList(); }
    async Task RestoreCurrent() { if (!string.IsNullOrWhiteSpace(settings.CurrentWallpaper) && File.Exists(settings.CurrentWallpaper)) await ApplyPath(settings.CurrentWallpaper); }
    async Task TimerTick() { if (busy) return; if (settings.AutoSwitchEnabled && DateTime.Now >= nextSwitch) await ApplyNext(); else await OnUi(() => { engine.Resize(); return Task.CompletedTask; }); }
    async Task ApplyNext() { Reload(); if (items.Count == 0) return; var pool = items.Where(x => x.Path != settings.CurrentWallpaper).ToList(); await ApplyPath((pool.Count > 0 ? pool[Random.Shared.Next(pool.Count)] : items[0]).Path); }
    async Task ApplyPath(string path)
    {
        if (!File.Exists(path)) return; await operationGate.WaitAsync(); busy = true;
        try { var item = new WallpaperItem { Path = path }; await OnUi(() => engine.ApplyAsync(item, settings)); nextSwitch = DateTime.Now.AddSeconds(settings.SwitchIntervalSeconds); tray.Text = $"MagicView Wallpaper · {item.Name}"; }
        catch (Exception ex) { store.Log(ex.ToString()); tray.ShowBalloonTip(3000, "壁纸应用失败", ex.Message, System.Windows.Forms.ToolTipIcon.Error); }
        finally { busy = false; operationGate.Release(); }
    }
    void ToggleAuto() { settings.AutoSwitchEnabled = !settings.AutoSwitchEnabled; store.Save(settings); tray.ShowBalloonTip(1500, "MagicView Wallpaper", settings.AutoSwitchEnabled ? "自动切换已启用" : "自动切换已暂停", System.Windows.Forms.ToolTipIcon.Info); }
    void OpenManager() { var exe = Environment.ProcessPath; if (exe != null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory }); }

    async Task PipeLoop()
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(shutdown.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                var request = await reader.ReadLineAsync(shutdown.Token);
                var command = string.IsNullOrWhiteSpace(request) ? null : JsonSerializer.Deserialize<EngineCommand>(request);
                var reply = command == null ? new EngineReply(false, engine.IsRunning, "命令为空。") : await Handle(command);
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply).AsMemory(), shutdown.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { store.Log("IPC: " + ex); try { await Task.Delay(1000, shutdown.Token); } catch (OperationCanceledException) { break; } }
        }
    }
    static NamedPipeServerStream CreatePipe()
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("无法取得当前 Windows 用户标识。");
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security, HandleInheritability.None, 0);
    }
    void RestoreTrayIcon()
    {
        Process? explorer = null;
        try { explorer = Process.GetProcessesByName("explorer").FirstOrDefault(x => x.SessionId == Process.GetCurrentProcess().SessionId); }
        catch { }
        if (explorer == null) return;
        if (!tray.Visible || explorerProcessId != explorer.Id)
        {
            tray.Visible = false; tray.Visible = true; explorerProcessId = explorer.Id;
            store.Log("托盘图标已注册。Explorer PID=" + explorerProcessId);
        }
    }
    async Task<EngineReply> Handle(EngineCommand command)
    {
        switch (command.Action)
        {
            case "Status": return Reply(true, "后台引擎运行中。");
            case "Apply": if (string.IsNullOrWhiteSpace(command.Path)) return Reply(false, "壁纸路径为空。"); await ApplyPath(command.Path); return Reply(engine.IsRunning, engine.IsRunning ? "壁纸已应用。" : "壁纸应用失败。");
            case "Next": await ApplyNext(); return Reply(engine.IsRunning, "已切换下一张。");
            case "ReloadSettings": Reload(); nextSwitch = DateTime.Now.AddSeconds(settings.SwitchIntervalSeconds); return Reply(true, "设置已重新加载。");
            case "StopWallpaper": await OnUi(() => { engine.StopCurrent(); return Task.CompletedTask; }); return Reply(true, "壁纸渲染已停止。");
            case "InstallStartup": return Reply(SetStartup(true), "开机启动设置已更新。");
            case "RemoveStartup": return Reply(SetStartup(false), "开机启动设置已更新。");
            case "Shutdown": _ = Task.Run(async () => { await Task.Delay(250); System.Windows.Forms.Application.Exit(); }); return Reply(true, "引擎正在退出。");
            default: return Reply(false, "未知命令。");
        }
    }
    EngineReply Reply(bool success, string message) => new(success, engine.IsRunning, message, settings.CurrentWallpaper, settings.AutoSwitchEnabled, IsStartupEnabled());
    bool SetStartup(bool enabled)
    {
        try
        {
            var exe = Environment.ProcessPath!; var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true };
            if (enabled) { foreach (var a in new[] { "/Create", "/TN", TaskName, "/SC", "ONLOGON", "/DELAY", "0000:10", "/RL", "HIGHEST", "/TR", $"\"{exe}\" --engine", "/F" }) psi.ArgumentList.Add(a); }
            else { foreach (var a in new[] { "/Delete", "/TN", TaskName, "/F" }) psi.ArgumentList.Add(a); }
            using var p = Process.Start(psi); p?.WaitForExit(5000); return p?.ExitCode == 0;
        }
        catch (Exception ex) { store.Log("Startup: " + ex); return false; }
    }
    static bool IsStartupEnabled()
    {
        try { using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Query /TN {TaskName}") { UseShellExecute = false, CreateNoWindow = true }); p?.WaitForExit(1500); return p?.ExitCode == 0; }
        catch { return false; }
    }
    Task OnUi(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async void Run()
        {
            try { await action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }
        if (uiDispatcher.InvokeRequired) uiDispatcher.BeginInvoke((Action)Run); else Run();
        return completion.Task;
    }
    protected override void Dispose(bool disposing) { if (disposing) Cleanup(); base.Dispose(disposing); }
    void Cleanup() { if (exiting) return; exiting = true; shutdown.Cancel(); timer.Dispose(); trayRecoveryTimer.Stop(); trayRecoveryTimer.Dispose(); engine.Dispose(); tray.Visible = false; tray.Dispose(); appIcon.Dispose(); uiDispatcher.Dispose(); store.Log("后台引擎已退出。"); }
    void ExitEngine() { Cleanup(); ExitThread(); }
}
