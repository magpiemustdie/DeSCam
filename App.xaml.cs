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
        // File logging disabled — was writing full stack traces to crash_log.txt
        // which grew unbounded.  The UI connects to RPCS3's remote process and
        // uses NtReadVirtualMemory; occasional STATUS_INVALID_HANDLE / access
        // violations are normal and handled, not crashes.
    }
}
