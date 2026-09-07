using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MagicViewWallpaper.App;

public sealed class ThumbnailService
{
    readonly string cacheRoot;
    readonly SemaphoreSlim gate = new(2);

    public ThumbnailService(SettingsStore store)
    {
        cacheRoot = Path.Combine(store.DataRoot, "Cache", "Thumbnails");
        Directory.CreateDirectory(cacheRoot);
    }

    public async Task<string?> GetOrCreateAsync(WallpaperItem item)
    {
        if (!item.IsVideo || !File.Exists(item.Path)) return null;
        var info = new FileInfo(item.Path);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{item.Path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")));
        var output = Path.Combine(cacheRoot, key + ".png");
        if (File.Exists(output) && new FileInfo(output).Length > 1024) return output;
        await gate.WaitAsync();
        try
        {
            if (File.Exists(output) && new FileInfo(output).Length > 1024) return output;
            var mpv = Path.Combine(AppContext.BaseDirectory, "Runtime", "mpv", "mpv.exe");
            if (!File.Exists(mpv)) return null;
            var psi = new ProcessStartInfo(mpv) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(mpv)! };
            foreach (var arg in new[] { "--no-config", "--start=10%", "--frames=1", "--no-audio", "--vf=scale=640:-2", "--of=image2", "--ovc=png", $"--o={output}", "--", item.Path }) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi); if (process == null) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { try { process.Kill(true); } catch { } return null; }
            return process.ExitCode == 0 && File.Exists(output) ? output : null;
        }
        catch { return null; }
        finally { gate.Release(); }
    }
}
