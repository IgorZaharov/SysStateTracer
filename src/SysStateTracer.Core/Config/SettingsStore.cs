using System.Text.Json;
using System.Text.Json.Serialization;

using SysStateTracer.Core.Ignore;

namespace SysStateTracer.Core.Config;

/// <summary>Loads and saves <see cref="AppSettings"/> as indented JSON.</summary>
public static class SettingsStore
{
    /// <summary>
    /// Reads settings from <paramref name="path"/>. A missing file gives first-run defaults, including
    /// the built-in ignore rules. An unreadable file is kept as <c>SysStateTracer.config.bad</c> and
    /// defaults are returned, so a broken config never stops the app from starting.
    /// </summary>
    public static AppSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return AppSettings.CreateDefault();
        }

        try
        {
            AppSettings? settings;
            using (var stream = File.OpenRead(path))
            {
                settings = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings);
            }

            return Normalize(settings ?? new AppSettings());
        }
        catch (JsonException ex)
        {
            Log.Warning($"Settings file is not valid JSON and was replaced by defaults: {path}", ex);
            TryKeepBadCopy(path);
            return AppSettings.CreateDefault();
        }
    }

    /// <summary>Writes settings atomically: a temp file first, then a rename over the old file.</summary>
    public static void Save(string path, AppSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        settings.FormatVersion = AppSettings.CurrentFormatVersion;
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
        }

        File.Move(temp, path, overwrite: true);
    }

    // Missing or empty values in the file fall back to the defaults.
    private static AppSettings Normalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.IgnoreListRemoteLocation))
        {
            settings.IgnoreListRemoteLocation = RemoteIgnoreSource.DefaultLocation;
        }

        settings.Scope ??= new SnapshotScope();
        settings.Scope.RegistryRoots ??= new SnapshotScope().RegistryRoots;
        return settings;
    }

    private static void TryKeepBadCopy(string path)
    {
        try
        {
            File.Copy(path, path + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not keep a copy of the broken settings file: {path}", ex);
        }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}
