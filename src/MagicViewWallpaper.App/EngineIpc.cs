using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MagicViewWallpaper.App;

public sealed record EngineCommand(string Action, string? Path = null);
public sealed record EngineReply(bool Success, bool Running, string Message, string? CurrentWallpaper = null, bool AutoSwitchEnabled = false, bool StartupEnabled = false);

public sealed class EngineClient
{
    const string PipeName = "MagicViewWallpaper.Engine.v1";

    public async Task<EngineReply> SendAsync(string action, string? path = null, int timeoutMs = 2500)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(timeoutMs);
            await pipe.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(new EngineCommand(action, path)).AsMemory(), timeout.Token);
            var response = await reader.ReadLineAsync(timeout.Token);
            return response == null ? new(false, false, "引擎响应为空。") : JsonSerializer.Deserialize<EngineReply>(response) ?? new(false, false, "引擎响应无效。");
        }
        catch (Exception ex) { return new(false, false, ex.Message); }
    }

    public async Task<EngineReply> EnsureStartedAsync()
    {
        var status = await SendAsync("Status", timeoutMs: 500);
        if (status.Success) return status;
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位应用程序。");
        try { Process.Start(new ProcessStartInfo(exe, "--engine") { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory }); }
        catch (System.ComponentModel.Win32Exception) { return new(false, false, "后台引擎需要管理员权限，授权已取消。"); }
        for (int i = 0; i < 20; i++) { await Task.Delay(300); status = await SendAsync("Status", timeoutMs: 500); if (status.Success) return status; }
        return new(false, false, "后台引擎启动超时。");
    }
}
