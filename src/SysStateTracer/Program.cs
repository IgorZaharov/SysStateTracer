using SysStateTracer.Core;
using SysStateTracer.Core.CommandLine;
using SysStateTracer.Core.Config;
using SysStateTracer.Core.Interop;

namespace SysStateTracer;

internal static class Program
{
    /// <summary>With arguments: runs one command-line switch and exits (plan §10.9). Without: opens the main window.</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var paths = AppPaths.Resolve();
        Log.Current = new FileLog(paths.LogFile);
        Log.Info($"{AppInfo.Name} {AppInfo.Version} started. Data folder: {paths.DataDirectory}" +
                 (paths.UsesFallback ? " (exe folder is not writable)" : string.Empty) +
                 $". Elevated: {ProcessPrivileges.IsElevated}.");

        if (args.Length > 0)
        {
            return RunCommandLine(args, paths);
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        var settings = SettingsStore.Load(paths.ConfigFile);

        ApplicationConfiguration.Initialize();
        Application.SetDefaultFont(Theming.UiFonts.Normal);
        Theming.ThemeManager.SetPreference(settings.Theme);
        Application.Run(new MainForm(paths, settings));

        Log.Info($"{AppInfo.Name} exited.");
        return 0;
    }

    private static int RunCommandLine(string[] args, AppPaths paths)
    {
        // Output goes to the console that started us (and always to the log). The prompt of an
        // interactive cmd window does not wait for a window application, hence the leading blank line.
        if (ConsoleAttach.ToParentConsole())
        {
            Console.WriteLine();
        }

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let the run stop cleanly and return exit code 5
            cancel.Cancel();
        };

        try
        {
            var environment = new CliEnvironment
            {
                Paths = paths,
                Settings = SettingsStore.Load(paths.ConfigFile),
                Output = Console.Out,
            };
            return CliRunner.Run(args, environment, cancel.Token);
        }
        catch (Exception ex)
        {
            Log.Error("Command line failed", ex);
            Console.Error.WriteLine($"Error: {FriendlyErrors.Describe(ex)} (details in {AppPaths.LogFileName})");
            return (int)CliExitCode.ReadWriteError;
        }
        finally
        {
            Log.Info($"{AppInfo.Name} exited.");
        }
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
