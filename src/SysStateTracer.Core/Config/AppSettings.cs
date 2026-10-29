using SysStateTracer.Core.Ignore;

namespace SysStateTracer.Core.Config;

/// <summary>Light, dark, or follow the Windows app theme (W5 Preferences).</summary>
public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>Column the snapshot list in W1 is sorted by; chosen by clicking a column header (F16).</summary>
public enum SnapshotSortOrder
{
    /// <summary>Creation time (default, newest first).</summary>
    Date,

    Name,

    Computer,

    Duration,

    Size,
}

/// <summary>
/// Contents of SysStateTracer.config. New settings are added as later steps need them;
/// unknown keys in an older or newer file are ignored.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentFormatVersion = 2;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>Default snapshot folder; null means beside SysStateTracer.exe.</summary>
    public string? SnapshotFolder { get; set; }

    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public SnapshotSortOrder SortOrder { get; set; } = SnapshotSortOrder.Date;

    /// <summary>Largest, newest or Z first; with <see cref="SortOrder"/> Date, newest on top (the default).</summary>
    public bool SortDescending { get; set; } = true;

    /// <summary>Size, position and maximized state of each window, by window name (F12).</summary>
    public Dictionary<string, WindowPlacement> Windows { get; set; } = [];

    /// <summary>Lines between rows and columns in the snapshot list and the comparison window.</summary>
    public bool ShowGridLines { get; set; }

    /// <summary>Every other row slightly shaded in the snapshot list and the comparison window.</summary>
    public bool ZebraStripes { get; set; }

    /// <summary>Comparison window: Show all (unchanged items too) instead of Show only changes.</summary>
    public bool ComparisonShowAll { get; set; }

    /// <summary>Comparison window: the list also shows the files and values of every folder or key below the selected one.</summary>
    public bool ComparisonIncludeSubfolders { get; set; }

    /// <summary>Which Global Ignore list is used (F1): the remote one, or the local file beside the exe.</summary>
    public IgnoreListSource IgnoreListSource { get; set; } = IgnoreListSource.Remote;

    /// <summary>URL (http/https) or file path (also \\server\share\file.txt) of the remote Global Ignore list.</summary>
    public string IgnoreListRemoteLocation { get; set; } = RemoteIgnoreSource.DefaultLocation;

    /// <summary>What New Snapshot (W3) and /new-snap read; saved from W3.</summary>
    public SnapshotScope Scope { get; set; } = new();

    public static AppSettings CreateDefault() => new();
}

/// <summary>Where a window was when it was last closed (F12), in screen pixels.</summary>
public sealed class WindowPlacement
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool Maximized { get; set; }
}

/// <summary>
/// The saved snapshot scope (plan D9 by default): all fixed drives, HKLM and HKU, Win32 services.
/// </summary>
public sealed class SnapshotScope
{
    /// <summary>Drives or folders such as "C:\" or "D:\Data"; null = every fixed drive at the time of the snapshot.</summary>
    public List<string>? FileSystemRoots { get; set; }

    /// <summary>"HKLM", "HKU" or keys below them.</summary>
    public List<string> RegistryRoots { get; set; } = ["HKLM", "HKU"];

    public bool IncludeServices { get; set; } = true;

    /// <summary>Kernel drivers as well as services (off by default, plan 2.16).</summary>
    public bool IncludeDrivers { get; set; }

    /// <summary>
    /// Folders and keys unchecked inside an included drive or hive in New Snapshot (W3), as rule lines
    /// ("FS:C:\Temp", "REG:HKLM\SOFTWARE\X"). They are left out like Global Ignore rules, for this scope only.
    /// </summary>
    public List<string> Excluded { get; set; } = [];
}
