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

    public static AppSettings CreateDefault() => new();
}
