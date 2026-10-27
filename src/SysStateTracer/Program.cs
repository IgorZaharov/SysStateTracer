using SysStateTracer.Core;
using SysStateTracer.Core.Config;
using SysStateTracer.Core.Interop;

namespace SysStateTracer;

internal static class Program
{
    // Command-line switches (/new-snap, /compare-snap, ...) are routed here from Step 39.
    [STAThread]
    private static void Main()
    {
        var paths = AppPaths.Resolve();
        Log.Current = new FileLog(paths.LogFile);
        Log.Info($"{AppInfo.Name} {AppInfo.Version} started. Data folder: {paths.DataDirectory}" +
                 (paths.UsesFallback ? " (exe folder is not writable)" : string.Empty) +
                 $". Elevated: {ProcessPrivileges.IsElevated}.");

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        var settings = SettingsStore.Load(paths.ConfigFile);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(paths, settings));

        Log.Info($"{AppInfo.Name} exited.");
    }

    private static void ReportFatal(Exception exception)
    {
        Log.Error("Unhandled exception on the UI thread", exception);
        MessageBox.Show(
            $"Something went wrong:{Environment.NewLine}{exception.Message}{Environment.NewLine}{Environment.NewLine}" +
            $"Details are in {AppPaths.LogFileName}.",
            AppInfo.Name,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
