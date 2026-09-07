using System.Configuration;
using System.Data;
using System.Runtime.InteropServices;
using System.Windows;

namespace MagicViewWallpaper.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    protected override void OnStartup(StartupEventArgs e)
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        base.OnStartup(e);
        if (e.Args.Any(x => x.Equals("--engine", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            using var singleInstance = new Mutex(true, @"Local\MagicViewWallpaper.Engine", out bool created);
            if (!created) { Shutdown(); return; }
            System.Windows.Forms.Application.EnableVisualStyles();
            using var host = new EngineHost();
            System.Windows.Forms.Application.Run(host);
            Shutdown();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}

