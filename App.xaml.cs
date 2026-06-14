using System.Windows;
using System.Windows.Threading;

namespace DeSCam;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Catch exceptions thrown from async void dispatcher callbacks (timer ticks, etc.)
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;   // prevent crash
            LogUnhandled("DispatcherUnhandledException", args.Exception);
        };

        // Catch exceptions propagated from Task continuations that nobody awaited
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();    // prevent crash
            LogUnhandled("UnobservedTaskException", args.Exception.InnerException ?? args.Exception);
        };

        // Catch anything else on the CLR level
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogUnhandled("UnhandledException (fatal=" + args.IsTerminating + ")", ex);
        };
    }

    static void LogUnhandled(string source, Exception ex)
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "crash_log.txt");
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n";
            System.IO.File.AppendAllText(path, line, System.Text.Encoding.UTF8);
        }
        catch { }
    }
}
