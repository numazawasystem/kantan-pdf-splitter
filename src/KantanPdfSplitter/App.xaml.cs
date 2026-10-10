using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace KantanPdfSplitter;

public partial class App : Application
{
    private Mutex? _running;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _running = new Mutex(false, @"Local\KantanPdfSplitter.AppRunning");
        if (Mutex.TryOpenExisting(@"Local\KantanPdfSplitter.Maintenance", out var maintenance))
        {
            maintenance.Dispose();
            MessageBox.Show("セットアップが実行中です。完了してから起動してください。", "かんたんPDF分割");
            Shutdown(1); return;
        }
        var splash = new SplashWindow();
        var timer = Stopwatch.StartNew();
        try
        {
            splash.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var main = new MainWindow();
            var remaining = TimeSpan.FromMilliseconds(1100) - timer.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
        }
        catch (Exception)
        {
            MessageBox.Show("アプリを起動できませんでした。再インストールしてお試しください。", "かんたんPDF分割", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
        finally { splash.Close(); }
    }
    protected override void OnExit(ExitEventArgs e) { _running?.Dispose(); base.OnExit(e); }
}
