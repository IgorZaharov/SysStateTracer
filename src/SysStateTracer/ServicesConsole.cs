using System.Diagnostics;
using System.Runtime.InteropServices;
using SysStateTracer.Core.Actions;

namespace SysStateTracer;

/// <summary>
/// "Jump to" for services (plan §10.6): opens the Services console and selects the service by typing its
/// display name into the list, which jumps to the matching row. The name is also put on the clipboard,
/// so if selecting fails the user can find it quickly.
/// </summary>
internal static partial class ServicesConsole
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Returns false when the console window did not appear in time (the name is on the clipboard then).</summary>
    public static async Task<bool> OpenAndSelectAsync(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        try
        {
            Clipboard.SetText(displayName);
        }
        catch (ExternalException)
        {
            // The clipboard is busy; selecting still works.
        }

        var started = DateTime.UtcNow;
        ShellLauncher.OpenServices();
        var window = nint.Zero;
        while (DateTime.UtcNow - started < WindowTimeout)
        {
            window = FindServicesWindow();
            if (window != 0)
            {
                break;
            }

            await Task.Delay(250);
        }

        if (window == 0)
        {
            return false;
        }

        // The list fills a moment after the window opens.
        await Task.Delay(1500);
        _ = SetForegroundWindow(window);
        await Task.Delay(200);
        if (GetForegroundWindow() != window)
        {
            return false;
        }

        SendKeys.SendWait(Escape(displayName));
        return true;
    }

    private static nint FindServicesWindow()
    {
        foreach (var process in Process.GetProcessesByName("mmc"))
        {
            using (process)
            {
                if (process.MainWindowHandle != 0 && process.MainWindowTitle.StartsWith("Services", StringComparison.OrdinalIgnoreCase))
                {
                    return process.MainWindowHandle;
                }
            }
        }

        return 0;
    }

    /// <summary>SendKeys treats + ^ % ~ ( ) { } [ ] as commands; they are sent in braces.</summary>
    private static string Escape(string text) =>
        string.Concat(text.Select(c => "+^%~(){}[]".Contains(c, StringComparison.Ordinal) ? "{" + c + "}" : c.ToString()));

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
}
