using System.ComponentModel;
using System.Diagnostics;
using SysStateTracer.Controls;

namespace SysStateTracer;

/// <summary>
/// SysStateTracer starts with the rights of the user who starts it (F9). A complete snapshot needs administrator
/// rights; this starts the program again with them (Windows shows its UAC prompt) and closes the current one.
/// </summary>
internal static class Elevation
{
    private const int ErrorCancelled = 1223;

    /// <summary>True when the new, elevated program started (the caller then closes); false when cancelled or failed.</summary>
    public static bool RestartAsAdministrator(IWin32Window? owner)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Core.Config.Log.Info("Restarted as administrator");
            Application.Exit();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false; // "No" in the UAC prompt
        }
        catch (Win32Exception ex)
        {
            MessageDialog.Error(owner, "SysStateTracer could not be started as administrator.", ex);
            return false;
        }
    }
}
