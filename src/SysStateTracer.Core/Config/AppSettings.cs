using SysStateTracer.Core.Ignore;

namespace SysStateTracer.Core.Config;

/// <summary>Light, dark, or follow the Windows app theme (W5 Preferences).</summary>
public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>Order of the snapshot list in W1.</summary>
public enum SnapshotSortOrder
{
    /// <summary>Newest first (default).</summary>
    Date,

    /// <summary>A to Z.</summary>
    Name,
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

    /// <summary>Which Global Ignore list is used (F1): the remote one, or the local file beside the exe.</summary>
    public IgnoreListSource IgnoreListSource { get; set; } = IgnoreListSource.Remote;

    /// <summary>URL (http/https) or file path (also \\server\share\file.txt) of the remote Global Ignore list.</summary>
    public string IgnoreListRemoteLocation { get; set; } = RemoteIgnoreSource.DefaultLocation;

    /// <summary>What New Snapshot (W3) and /new-snap read; saved from W3.</summary>
    public SnapshotScope Scope { get; set; } = new();

    public static AppSettings CreateDefault() => new();
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
