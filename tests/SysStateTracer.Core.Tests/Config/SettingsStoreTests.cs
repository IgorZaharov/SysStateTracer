using SysStateTracer.Core.Config;
using SysStateTracer.Core.Ignore;

namespace SysStateTracer.Core.Tests.Config;

public class SettingsStoreTests
{
    [Fact]
    public void Load_MissingFile_GivesDefaults()
    {
        using var dir = new TempDirectory();

        var settings = SettingsStore.Load(dir.File(AppPaths.ConfigFileName));

        Assert.Null(settings.SnapshotFolder);
        Assert.Equal(ThemePreference.System, settings.Theme);
        Assert.Equal(SnapshotSortOrder.Date, settings.SortOrder);
        Assert.Equal(IgnoreListSource.Remote, settings.IgnoreListSource);
        Assert.Equal(RemoteIgnoreSource.DefaultLocation, settings.IgnoreListRemoteLocation);
        Assert.False(settings.ShowGridLines);
        Assert.False(settings.ZebraStripes);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEverySetting()
    {
        using var dir = new TempDirectory();
        var path = dir.File(AppPaths.ConfigFileName);
        var original = new AppSettings
        {
            SnapshotFolder = @"D:\Snapshots",
            Theme = ThemePreference.Dark,
            SortOrder = SnapshotSortOrder.Name,
            IgnoreListSource = IgnoreListSource.Local,
            IgnoreListRemoteLocation = @"\\server\share\ignore.txt",
            ShowGridLines = true,
            ZebraStripes = true,
        };

        SettingsStore.Save(path, original);
        var loaded = SettingsStore.Load(path);

        Assert.Equal(original.SnapshotFolder, loaded.SnapshotFolder);
        Assert.Equal(original.Theme, loaded.Theme);
        Assert.Equal(original.SortOrder, loaded.SortOrder);
        Assert.Equal(original.IgnoreListSource, loaded.IgnoreListSource);
        Assert.Equal(original.IgnoreListRemoteLocation, loaded.IgnoreListRemoteLocation);
        Assert.True(loaded.ShowGridLines);
        Assert.True(loaded.ZebraStripes);
        Assert.Equal(AppSettings.CurrentFormatVersion, loaded.FormatVersion);
    }

    [Fact]
    public void Save_WritesReadableJsonWithEnumNames()
    {
        using var dir = new TempDirectory();
        var path = dir.File(AppPaths.ConfigFileName);

        SettingsStore.Save(path, new AppSettings { Theme = ThemePreference.Light });
        var json = File.ReadAllText(path);

        Assert.Contains("\"theme\": \"Light\"", json, StringComparison.Ordinal);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Load_IgnoresUnknownKeysAndComments()
    {
        using var dir = new TempDirectory();
        var path = dir.File(AppPaths.ConfigFileName);
        File.WriteAllText(path, """
            {
              // written by a newer version
              "theme": "Dark",
              "someFutureSetting": 42,
            }
            """);

        var settings = SettingsStore.Load(path);

        Assert.Equal(ThemePreference.Dark, settings.Theme);
    }

    [Fact]
    public void Load_BrokenFile_GivesDefaultsAndKeepsACopy()
    {
        using var dir = new TempDirectory();
        var path = dir.File(AppPaths.ConfigFileName);
        File.WriteAllText(path, "{ this is not json");

        var settings = SettingsStore.Load(path);

        Assert.Equal(ThemePreference.System, settings.Theme);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Load_EmptyRemoteLocation_BecomesDefault()
    {
        using var dir = new TempDirectory();
        var path = dir.File(AppPaths.ConfigFileName);
        File.WriteAllText(path, """{ "ignoreListSource": "Local", "ignoreListRemoteLocation": "" }""");

        var settings = SettingsStore.Load(path);

        Assert.Equal(IgnoreListSource.Local, settings.IgnoreListSource);
        Assert.Equal(RemoteIgnoreSource.DefaultLocation, settings.IgnoreListRemoteLocation);
    }
}
