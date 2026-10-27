using System.Buffers;
using System.Globalization;

namespace SysStateTracer.Core.Snapshots;

/// <summary>Snapshot names and file names: <c>YYYYMMDD_HH-MM-SS_COMPUTERNAME.stsnap</c>.</summary>
public static class SnapshotNaming
{
    public const string FileExtension = ".stsnap";

    /// <summary>Longest name accepted for a snapshot (without extension), well inside MAX_PATH.</summary>
    public const int MaxNameLength = 200;

    private static readonly SearchValues<char> InvalidFileNameChars =
        SearchValues.Create(Path.GetInvalidFileNameChars());

    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>Default name for a snapshot taken now on this computer.</summary>
    public static string DefaultName() => DefaultName(DateTime.Now, Environment.MachineName);

    /// <summary>Default name for a snapshot taken at <paramref name="localTime"/> on <paramref name="computerName"/>.</summary>
    public static string DefaultName(DateTime localTime, string computerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(computerName);
        return string.Create(CultureInfo.InvariantCulture, $"{localTime:yyyyMMdd_HH-mm-ss}_{computerName}");
    }

    /// <summary>
    /// True when <paramref name="name"/> can be used as a snapshot file name on Windows:
    /// not empty, no invalid characters, no leading/trailing spaces or trailing dot, not a device name.
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return false;
        }

        if (name[0] == ' ' || name[^1] == ' ' || name[^1] == '.')
        {
            return false;
        }

        if (name.AsSpan().ContainsAny(InvalidFileNameChars))
        {
            return false;
        }

        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = dot >= 0 ? name[..dot] : name;
        return !ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }
}
