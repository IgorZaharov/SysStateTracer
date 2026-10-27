using System.Diagnostics;
using Microsoft.Win32;

namespace SysStateTracer.Core.Actions;

/// <summary>A program to start: what <see cref="ShellLauncher"/> would run, also checked by tests.</summary>
public sealed record LaunchCommand(string FileName, string Arguments);

/// <summary>
/// "Jump to" (plan §10.6): opens the item in Explorer, Regedit or the Services console. A deleted item
/// opens at its nearest existing parent.
/// </summary>
public static class ShellLauncher
{
    private const string RegeditSettings = @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit";

    /// <summary>Explorer with the item selected, or the nearest existing folder opened.</summary>
    public static LaunchCommand ExplorerCommand(string path, Func<string, bool>? exists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        if (exists(path))
        {
            return new LaunchCommand("explorer.exe", $"/select,\"{path}\"");
        }

        var parent = Path.GetDirectoryName(path.TrimEnd('\\'));
        while (!string.IsNullOrEmpty(parent) && !exists(parent))
        {
            parent = Path.GetDirectoryName(parent);
        }

        return new LaunchCommand("explorer.exe", $"\"{parent ?? Path.GetPathRoot(path)}\"");
    }

    public static void OpenInExplorer(string path) => Start(ExplorerCommand(path));

    /// <summary>
    /// Regedit's "LastKey" for a snapshot key path: "HKLM\SOFTWARE\X" → "Computer\HKEY_LOCAL_MACHINE\SOFTWARE\X".
    /// The current user's key under HKU is shown as HKEY_CURRENT_USER, as Regedit does.
    /// </summary>
    public static string RegeditLastKey(string keyPath, string? currentUserSid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        currentUserSid ??= System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        if (currentUserSid is not null)
        {
            var prefix = @"HKU\" + currentUserSid;
            if (keyPath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                keyPath.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
            {
                keyPath = "HKCU" + keyPath[prefix.Length..];
            }
        }

        return @"Computer\" + Export.RegFile.KeyName(keyPath);
    }

    /// <summary>Opens Regedit at <paramref name="keyPath"/> (or its nearest existing parent): sets LastKey, then starts "regedit.exe /m".</summary>
    public static void OpenInRegedit(string keyPath)
    {
        var existing = NearestExistingKey(keyPath);
        using (var settings = Registry.CurrentUser.CreateSubKey(RegeditSettings, writable: true))
        {
            settings.SetValue("LastKey", RegeditLastKey(existing), RegistryValueKind.String);
        }

        Start(new LaunchCommand("regedit.exe", "/m"));
    }

    /// <summary>Opens the Services console. Selecting the service in it is done by the UI (UI Automation).</summary>
    public static void OpenServices() => Start(new LaunchCommand("mmc.exe", "services.msc"));

    /// <summary>The key itself if it exists in the live registry, else its nearest existing parent.</summary>
    internal static string NearestExistingKey(string keyPath)
    {
        var path = keyPath.TrimEnd('\\');
        var slash = path.IndexOf('\\', StringComparison.Ordinal);
        RegistryHive? hive = (slash < 0 ? path : path[..slash]).ToUpperInvariant() switch
        {
            "HKLM" => RegistryHive.LocalMachine,
            "HKU" => RegistryHive.Users,
            _ => null,
        };
        if (hive is null || slash < 0)
        {
            return path;
        }

        using var root = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
        while (true)
        {
            slash = path.IndexOf('\\', StringComparison.Ordinal);
            if (slash < 0)
            {
                return path;
            }

            using var key = root.OpenSubKey(path[(slash + 1)..]);
            if (key is not null)
            {
                return path;
            }

            path = path[..path.LastIndexOf('\\')];
        }
    }

    private static void Start(LaunchCommand command)
    {
        using var process = Process.Start(new ProcessStartInfo(command.FileName, command.Arguments) { UseShellExecute = true });
    }
}
