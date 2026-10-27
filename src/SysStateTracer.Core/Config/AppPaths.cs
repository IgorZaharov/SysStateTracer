namespace SysStateTracer.Core.Config;

/// <summary>
/// Where SysStateTracer keeps its files. Everything lives beside SysStateTracer.exe; if that folder
/// is not writable (e.g. under Program Files), %LOCALAPPDATA%\SysStateTracer is used instead.
/// </summary>
public sealed class AppPaths
{
    public const string ConfigFileName = "SysStateTracer.config";
    public const string CacheFileName = "SysStateTracer.cache";
    public const string LogFileName = "SysStateTracer.log";
    public const string LocalIgnoreFileName = "SysStateTracer.ignore.txt";
    public const string FilesFolderName = "Files";
    public const string BackupsFolderName = "Backups";

    private AppPaths(string exeDirectory, string dataDirectory, bool usesFallback)
    {
        ExeDirectory = exeDirectory;
        DataDirectory = dataDirectory;
        UsesFallback = usesFallback;
    }

    /// <summary>Folder that contains SysStateTracer.exe.</summary>
    public string ExeDirectory { get; }

    /// <summary>Folder for config, cache, log, Files\ and Backups\ (and snapshots unless configured).</summary>
    public string DataDirectory { get; }

    /// <summary>True when the exe folder was not writable and %LOCALAPPDATA% is used.</summary>
    public bool UsesFallback { get; }

    public string ConfigFile => Path.Combine(DataDirectory, ConfigFileName);

    public string CacheFile => Path.Combine(DataDirectory, CacheFileName);

    public string LogFile => Path.Combine(DataDirectory, LogFileName);

    /// <summary>The local Global Ignore list (F1).</summary>
    public string LocalIgnoreFile => Path.Combine(DataDirectory, LocalIgnoreFileName);

    public string FilesDirectory => Path.Combine(DataDirectory, FilesFolderName);

    public string BackupsDirectory => Path.Combine(DataDirectory, BackupsFolderName);

    /// <summary>Snapshot folder: the one set in Preferences, otherwise <see cref="DataDirectory"/>.</summary>
    public string SnapshotDirectory(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return string.IsNullOrWhiteSpace(settings.SnapshotFolder) ? DataDirectory : settings.SnapshotFolder;
    }

    /// <summary>Paths for the running exe.</summary>
    public static AppPaths Resolve() =>
        Resolve(DefaultExeDirectory(), DefaultFallbackDirectory(), IsDirectoryWritable);

    /// <summary>Paths for a given exe folder; the writability probe is injectable for tests.</summary>
    public static AppPaths Resolve(string exeDirectory, string fallbackDirectory, Func<string, bool> isWritable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackDirectory);
        ArgumentNullException.ThrowIfNull(isWritable);

        var exe = Path.GetFullPath(exeDirectory);
        if (isWritable(exe))
        {
            return new AppPaths(exe, exe, usesFallback: false);
        }

        var fallback = Path.GetFullPath(fallbackDirectory);
        Directory.CreateDirectory(fallback);
        return new AppPaths(exe, fallback, usesFallback: true);
    }

    /// <summary>True when a file can be created (and deleted) in <paramref name="directory"/>.</summary>
    public static bool IsDirectoryWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe, bufferSize: 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static string DefaultExeDirectory() =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private static string DefaultFallbackDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name);
}
